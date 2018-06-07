﻿/*
Copyright (c) 2018, Lars Brubaker, John Lewin
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies,
either expressed or implied, of the FreeBSD Project.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using ClipperLib;
using MatterHackers.Agg.Transform;
using MatterHackers.Agg.UI;
using MatterHackers.Agg.VertexSource;
using MatterHackers.DataConverters2D;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DesignTools.Operations;
using MatterHackers.VectorMath;
using Newtonsoft.Json;

namespace MatterHackers.MatterControl.DesignTools
{
	using Polygon = List<IntPoint>;
	using Polygons = List<List<IntPoint>>;
	public enum BaseTypes { None, Rectangle, Circle, /* Oval, Frame,*/ Outline, };

	public class BaseObject3D : Object3D, IPublicPropertyObject, IPropertyGridModifier
	{
		readonly double scalingForClipper = 1000;

		public BaseObject3D()
		{
			Name = "Base".Localize();
		}

		public override bool CanRemove => true;
		public override bool CanApply => true;

		public BaseTypes CurrentBaseType { get; set; }
		public double BaseSize { get; set; } = 3;
		public double InfillAmount { get; set; } = 3;
		public double ExtrusionHeight { get; set; } = 5;

		public override void Apply(UndoBuffer undoBuffer)
		{
			OperationSource.Apply(this);

			base.Apply(undoBuffer);
		}

		public override void Remove(UndoBuffer undoBuffer)
		{
			OperationSource.Remove(this);

			base.Remove(undoBuffer);
		}

		[JsonIgnore]
		public IVertexSource VertexSource
		{
			get
			{
				var vertexSource = (IPathObject)this.Descendants<IObject3D>().FirstOrDefault((i) => i is IPathObject);
				return vertexSource?.VertexSource;
			}
			set
			{
				var vertexSource = this.Children.OfType<IPathObject>().FirstOrDefault();
				if (vertexSource != null)
				{
					vertexSource.VertexSource = value;
				}
			}
		}

		public static BaseObject3D Create()
		{
			var item = new BaseObject3D();
			item.Rebuild(null);
			return item;
		}

		public override void OnInvalidate(InvalidateArgs invalidateType)
		{
			if ((invalidateType.InvalidateType.HasFlag(InvalidateType.Content)
				|| invalidateType.InvalidateType.HasFlag(InvalidateType.Matrix)
				|| invalidateType.InvalidateType.HasFlag(InvalidateType.Path)
				|| invalidateType.InvalidateType.HasFlag(InvalidateType.Mesh))
				&& invalidateType.Source != this
				&& !RebuildSuspended)
			{
				Rebuild(null);
			}
			else
			{
				base.OnInvalidate(invalidateType);
			}
		}

		public override void Rebuild(UndoBuffer undoBuffer)
		{
			this.DebugDepth("Rebuild");

			SuspendRebuild();

			var aabb = this.GetAxisAlignedBoundingBox();

			Children.Modify((list) =>
			{
				list.RemoveAll((i) => !(i is OperationSource));
			});

			// Fall back to sibling content if VertexSource is unset
			var vertexSource = this.VertexSource;

			// Convert VertexSource into expected Polygons
			Polygons polygonShape = (vertexSource == null) ? null : vertexSource.CreatePolygons();

			GenerateBase(polygonShape);

			if (aabb.ZSize > 0)
			{
				// If the part was already created and at a height, maintain the height.
				PlatingHelper.PlaceMeshAtHeight(this, aabb.minXYZ.Z);
			}
			ResumeRebuild();

			Invalidate(new InvalidateArgs(this, InvalidateType.Mesh));
		}

		private static Polygon GetBoundingPolygon(Polygons basePolygons)
		{
			var min = new IntPoint(long.MaxValue, long.MaxValue);
			var max = new IntPoint(long.MinValue, long.MinValue);

			foreach (Polygon polygon in basePolygons)
			{
				foreach (IntPoint point in polygon)
				{
					min.X = Math.Min(point.X - 10, min.X);
					min.Y = Math.Min(point.Y - 10, min.Y);
					max.X = Math.Max(point.X + 10, max.X);
					max.Y = Math.Max(point.Y + 10, max.Y);
				}
			}

			var boundingPoly = new Polygon();
			boundingPoly.Add(min);
			boundingPoly.Add(new IntPoint(min.X, max.Y));
			boundingPoly.Add(max);
			boundingPoly.Add(new IntPoint(max.X, min.Y));

			return boundingPoly;
		}

		private static Polygon GetBoundingCircle(Polygons basePolygons)
		{
			var min = new IntPoint(long.MaxValue, long.MaxValue);
			var max = new IntPoint(long.MinValue, long.MinValue);

			foreach (Polygon polygon in basePolygons)
			{
				foreach (IntPoint point in polygon)
				{
					min.X = Math.Min(point.X, min.X);
					min.Y = Math.Min(point.Y, min.Y);
					max.X = Math.Max(point.X, max.X);
					max.Y = Math.Max(point.Y, max.Y);
				}
			}

			IntPoint center = (max - min) / 2 + min;
			long maxRadius = 0;

			foreach (Polygon polygon in basePolygons)
			{
				foreach (IntPoint point in polygon)
				{
					long radius = (point - center).Length();
					if (radius > maxRadius)
					{
						maxRadius = radius;
					}
				}
			}

			var boundingCircle = new Polygon();
			int numPoints = 100;

			for (int i = 0; i < numPoints; i++)
			{
				double angle = i / 100.0 * Math.PI * 2.0;
				IntPoint newPointOnCircle = new IntPoint(Math.Cos(angle) * maxRadius, Math.Sin(angle) * maxRadius) + center;
				boundingCircle.Add(newPointOnCircle);
			}

			return boundingCircle;
		}

		private static PolyTree GetPolyTree(Polygons basePolygons)
		{
			// create a bounding polygon to clip against
			Polygon boundingPoly = GetBoundingPolygon(basePolygons);

			var polyTreeForTrace = new PolyTree();

			var clipper = new Clipper();
			clipper.AddPaths(basePolygons, PolyType.ptSubject, true);
			clipper.AddPath(boundingPoly, PolyType.ptClip, true);
			clipper.Execute(ClipType.ctIntersection, polyTreeForTrace);

			return polyTreeForTrace;
		}

		public void GenerateBase(Polygons polygonShape)
		{
			if (polygonShape.Select(p => p.Count).Sum() > 3)
			{
				Polygons polysToOffset = new Polygons();

				switch (CurrentBaseType)
				{
					case BaseTypes.Rectangle:
						polysToOffset.Add(GetBoundingPolygon(polygonShape));
						break;

					case BaseTypes.Circle:
						polysToOffset.Add(GetBoundingCircle(polygonShape));
						break;

					case BaseTypes.Outline:
						PolyTree polyTreeForBase = GetPolyTree(polygonShape);
						foreach (PolyNode polyToOffset in polyTreeForBase.Childs)
						{
							polysToOffset.Add(polyToOffset.Contour);
						}
						break;
				}

				if (polysToOffset.Count > 0)
				{
					Polygons basePolygons;

					if (CurrentBaseType == BaseTypes.Outline
						&& InfillAmount > 0)
					{
						basePolygons = Offset(polysToOffset, (BaseSize + InfillAmount) * scalingForClipper);
						basePolygons = Offset(basePolygons, -InfillAmount * scalingForClipper);
					}
					else
					{
						basePolygons = Offset(polysToOffset, BaseSize * scalingForClipper);
					}

					basePolygons = ClipperLib.Clipper.CleanPolygons(basePolygons, 10);

					VertexStorage rawVectorShape = basePolygons.PolygonToPathStorage();
					var vectorShape = new VertexSourceApplyTransform(rawVectorShape, Affine.NewScaling(1.0 / scalingForClipper));

					var baseObject = new Object3D()
					{
						Mesh = VertexSourceToMesh.Extrude(vectorShape, zHeight: ExtrusionHeight)
					};
					Children.Add(baseObject);
					baseObject.Mesh.Translate(new Vector3(0, 0, -ExtrusionHeight));
				}
				else
				{
					// clear the mesh
					Mesh = null;
				}
			}
		}

		public Polygons Offset(Polygons polygons, double distance)
		{
			var offseter = new ClipperOffset();
			offseter.AddPaths(polygons, JoinType.jtRound, EndType.etClosedPolygon);

			var solution = new Polygons();
			offseter.Execute(ref solution, distance);

			return solution;
		}

		public void UpdateControls(PPEContext context)
		{
			//context.GetEditRow((this.ID, nameof(InfillAmount))).Visible = CurrentBaseType == BaseTypes.Outline;
		}
	}
}