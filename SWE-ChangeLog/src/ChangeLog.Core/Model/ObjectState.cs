using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;

namespace ChangeLog.Core.Model
{
    /// <summary>Identity of a project object: kind + SOLIDWORKS Electrical database ID.</summary>
    public struct ObjectKey : IEquatable<ObjectKey>
    {
        public ObjectKey(ObjectKind kind, int id)
        {
            Kind = kind;
            Id = id;
        }

        public ObjectKind Kind { get; }
        public int Id { get; }

        public bool Equals(ObjectKey other) => Kind == other.Kind && Id == other.Id;
        public override bool Equals(object obj) => obj is ObjectKey k && Equals(k);
        public override int GetHashCode() => ((int)Kind * 397) ^ Id;
        public override string ToString() => Kind + ":" + Id.ToString(CultureInfo.InvariantCulture);
    }

    [DataContract]
    public sealed class FieldValue
    {
        public FieldValue() { }

        public FieldValue(string name, string value)
        {
            Name = name;
            Value = value;
        }

        [DataMember(Name = "n", Order = 0)] public string Name { get; set; }
        [DataMember(Name = "v", Order = 1, EmitDefaultValue = false)] public string Value { get; set; }
    }

    /// <summary>Drawing geometry in page units (SWE millimetres, Y-up).</summary>
    [DataContract]
    public sealed class Geometry
    {
        [DataMember(Name = "s", Order = 0)] public GeometryShape Shape { get; set; }
        [DataMember(Name = "x1", Order = 1)] public double X1 { get; set; }
        [DataMember(Name = "y1", Order = 2)] public double Y1 { get; set; }
        [DataMember(Name = "x2", Order = 3)] public double X2 { get; set; }
        [DataMember(Name = "y2", Order = 4)] public double Y2 { get; set; }
        [DataMember(Name = "r", Order = 5, EmitDefaultValue = false)] public double Rotation { get; set; }

        public static Geometry Rect(double x, double y, double width, double height, double rotation)
        {
            return new Geometry { Shape = GeometryShape.Rect, X1 = x, Y1 = y, X2 = x + width, Y2 = y + height, Rotation = rotation };
        }

        public static Geometry Point(double x, double y, double rotation)
        {
            return new Geometry { Shape = GeometryShape.Point, X1 = x, Y1 = y, X2 = x, Y2 = y, Rotation = rotation };
        }

        public static Geometry Segment(double x1, double y1, double x2, double y2)
        {
            return new Geometry { Shape = GeometryShape.Segment, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };
        }

        public string Describe()
        {
            var c = CultureInfo.InvariantCulture;
            switch (Shape)
            {
                case GeometryShape.Segment:
                    return string.Format(c, "({0:0.##}, {1:0.##}) → ({2:0.##}, {3:0.##})", X1, Y1, X2, Y2);
                case GeometryShape.None:
                    return string.Empty;
                default:
                    return Math.Abs(Rotation) > 0.001
                        ? string.Format(c, "({0:0.##}, {1:0.##}) @ {2:0.#}°", X1, Y1, Rotation)
                        : string.Format(c, "({0:0.##}, {1:0.##})", X1, Y1);
            }
        }
    }

    /// <summary>
    /// Snapshot of one project object, decoupled from COM. This is the unit the
    /// recorder, diff engine and baselines operate on.
    /// </summary>
    [DataContract]
    public sealed class ObjectState
    {
        public const string GeometryField = "Position";

        public ObjectState()
        {
            Fields = new List<FieldValue>();
        }

        [DataMember(Name = "k", Order = 0)] public ObjectKind Kind { get; set; }
        [DataMember(Name = "id", Order = 1)] public int Id { get; set; }
        [DataMember(Name = "l", Order = 2, EmitDefaultValue = false)] public string Label { get; set; }
        [DataMember(Name = "p", Order = 3, EmitDefaultValue = false)] public int? PageId { get; set; }
        [DataMember(Name = "t", Order = 4, EmitDefaultValue = false)] public string TypeName { get; set; }
        [DataMember(Name = "f", Order = 5)] public List<FieldValue> Fields { get; set; }
        [DataMember(Name = "g", Order = 6, EmitDefaultValue = false)] public Geometry Geometry { get; set; }

        public ObjectKey Key => new ObjectKey(Kind, Id);

        public string Get(string name)
        {
            var f = Fields?.FirstOrDefault(x => x.Name == name);
            return f?.Value;
        }

        /// <summary>Sets a field; empty values are not stored (keeps diffs and files small).</summary>
        public ObjectState Set(string name, string value)
        {
            if (Fields == null) Fields = new List<FieldValue>();
            Fields.RemoveAll(x => x.Name == name);
            if (!string.IsNullOrEmpty(value)) Fields.Add(new FieldValue(name, value));
            return this;
        }

        public ObjectState Set(string name, double value)
        {
            return Set(name, value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        public ObjectState Set(string name, int value)
        {
            return Set(name, value.ToString(CultureInfo.InvariantCulture));
        }

        public ObjectState Clone()
        {
            return new ObjectState
            {
                Kind = Kind,
                Id = Id,
                Label = Label,
                PageId = PageId,
                TypeName = TypeName,
                Fields = (Fields ?? new List<FieldValue>()).Select(f => new FieldValue(f.Name, f.Value)).ToList(),
                Geometry = Geometry == null ? null : new Geometry
                {
                    Shape = Geometry.Shape, X1 = Geometry.X1, Y1 = Geometry.Y1, X2 = Geometry.X2, Y2 = Geometry.Y2, Rotation = Geometry.Rotation
                }
            };
        }

        public string DisplayLabel => string.IsNullOrEmpty(Label) ? Kind + " #" + Id.ToString(CultureInfo.InvariantCulture) : Label;
    }
}
