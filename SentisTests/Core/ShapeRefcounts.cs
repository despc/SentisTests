using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using VRage.Game.Models;

namespace SentisTests.Core
{
    /// <summary>
    /// The reference counts of the collision shapes of loaded models, the highest first.
    ///
    /// A grid wraps each block's model collision (the model's shape, or each child of its list or MOPP shape) in a
    /// transform shape, and every block of that model on every grid points at the same Havok shape. Havok's reference
    /// count is 16 bits: a crash of the stand (30.09.2026, access violation in a physics job, Havok+0xa2cfd8) had one
    /// such box shape referenced 91460 times with a count of -3 - freed while grids still used it.
    /// </summary>
    public static class ShapeRefcounts
    {
        private static readonly FieldInfo ModelsField =
            typeof(MyModels).GetField("m_models", BindingFlags.Static | BindingFlags.NonPublic);

        public static string Top(int count)
        {
            var rows = new List<(string Model, int Child, int References)>();
            if (!(ModelsField?.GetValue(null) is IDictionary models)) return "MyModels.m_models not found";
            lock (models.SyncRoot)
            {
                foreach (var value in models.Values)
                {
                    var model = value as MyModel;
                    var shapes = model?.HavokCollisionShapes;
                    if (shapes == null) continue;
                    for (var i = 0; i < shapes.Length; i++)
                    {
                        var shape = shapes[i];
                        if (shape.IsZero) continue;
                        rows.Add((model.AssetName, -1, shape.ReferenceCount));
                        if (shape.ShapeType == HkShapeType.List)
                        {
                            var list = (HkListShape)shape;
                            for (var c = 0; c < list.TotalChildrenCount; c++)
                                rows.Add((model.AssetName, c, list.GetChildByIndex(c).ReferenceCount));
                        }
                        else if (shape.ShapeType == HkShapeType.Mopp)
                        {
                            var collection = ((HkMoppBvTreeShape)shape).ShapeCollection;
                            for (var c = 0; c < collection.ShapeCount; c++)
                                rows.Add((model.AssetName, c, collection.GetShape((uint)c, null).ReferenceCount));
                        }
                    }
                }
            }
            var negative = rows.Count(r => r.References <= 0);
            return "SHAPE REFCOUNTS | " + rows.Count + " model shapes, " + negative + " at 0 or below | highest: " +
                   string.Join(", ", rows.OrderByDescending(r => Math.Abs(r.References)).Take(count)
                       .Select(r => System.IO.Path.GetFileNameWithoutExtension(r.Model) + (r.Child >= 0 ? "#" + r.Child : "") + "=" + r.References));
        }
    }
}
