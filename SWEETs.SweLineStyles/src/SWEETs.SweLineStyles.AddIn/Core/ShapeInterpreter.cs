using System;
using System.Collections.Generic;

namespace SWEETs.SweLineStyles.AddIn.Core
{
    /// <summary>
    /// Turns shape specification bytes into lines and arcs (shape units, insertion point at 0,0).
    /// Supports every shape-specification code: vector bytes (length/direction), 0 end, 1/2 pen down/up,
    /// 3/4 divide/multiply scale, 5/6 push/pop, 7 subshape, 8/9 displacements, 10 octant arc, 11 fractional arc,
    /// 12/13 bulge arcs, 14 vertical-text flag (the next command is skipped, as for horizontal text).
    /// </summary>
    public static class ShapeInterpreter
    {
        /// <summary>Direction vectors for the 16 vector-byte directions (0 = east, 4 = north, 8 = west, C = south).</summary>
        public static readonly Pt[] Directions =
        {
            new Pt(1, 0), new Pt(1, .5), new Pt(1, 1), new Pt(.5, 1), new Pt(0, 1), new Pt(-.5, 1), new Pt(-1, 1), new Pt(-1, .5),
            new Pt(-1, 0), new Pt(-1, -.5), new Pt(-1, -1), new Pt(-.5, -1), new Pt(0, -1), new Pt(.5, -1), new Pt(1, -1), new Pt(1, -.5),
        };

        public static List<ShapePrimitive> Decode(byte[] bytes, Func<int, byte[]> subshape = null)
        {
            var result = new List<ShapePrimitive>();
            var st = new State();
            Run(bytes ?? new byte[0], st, result, subshape, 0);
            return result;
        }

        private sealed class State
        {
            public Pt Pos;
            public bool Pen = true;
            public double Scale = 1;
            public readonly Stack<Pt> Stack = new Stack<Pt>();
        }

        private static sbyte S(byte b) => unchecked((sbyte)b);

        private static void Run(byte[] b, State st, List<ShapePrimitive> output, Func<int, byte[]> subshape, int depth)
        {
            int i = 0;
            bool skip = false;
            byte Next() => i < b.Length ? b[i++] : throw new FormatException("Specification ends in the middle of a command.");

            while (i < b.Length)
            {
                var o = skip ? null : output;    // code 14: parse the next command without drawing or moving
                var save = skip ? (Pt?)st.Pos : null;
                bool penBefore = st.Pen; double scaleBefore = st.Scale;
                skip = false;

                int c = Next();
                switch (c)
                {
                    case 0: return;
                    case 1: st.Pen = true; break;
                    case 2: st.Pen = false; break;
                    case 3: { int d = Next(); if (d != 0) st.Scale /= d; break; }
                    case 4: { int m = Next(); st.Scale *= m; break; }
                    case 5: st.Stack.Push(st.Pos); break;
                    case 6: if (st.Stack.Count > 0) st.Pos = st.Stack.Pop(); break;
                    case 7:
                    {
                        int n = Next();
                        var sub = subshape?.Invoke(n);
                        if (sub != null && depth < 8 && o != null) Run(sub, st, o, subshape, depth + 1);
                        break;
                    }
                    case 8: Move(st, o, S(Next()), S(Next())); break;
                    case 9:
                        while (true)
                        {
                            int dx = S(Next()), dy = S(Next());
                            if (dx == 0 && dy == 0) break;
                            Move(st, o, dx, dy);
                        }
                        break;
                    case 10:
                    {
                        double r = Next() * st.Scale;
                        int spec = Next();
                        OctantArc(st, o, r, spec, 0, 0);
                        break;
                    }
                    case 11:
                    {
                        int so = Next(), eo = Next(), hr = Next(), lr = Next(), spec = Next();
                        OctantArc(st, o, (hr * 256 + lr) * st.Scale, spec, so, eo);
                        break;
                    }
                    case 12: Bulge(st, o, S(Next()), S(Next()), S(Next())); break;
                    case 13:
                        while (true)
                        {
                            int dx = S(Next()), dy = S(Next());
                            if (dx == 0 && dy == 0) break;
                            Bulge(st, o, dx, dy, S(Next()));
                        }
                        break;
                    case 14: skip = true; break;
                    default:
                    {
                        double len = (c >> 4) * st.Scale;
                        var d = Directions[c & 0x0F];
                        var to = st.Pos + d * len;
                        if (st.Pen && o != null) o.Add(new LinePrim(st.Pos, to));
                        st.Pos = to;
                        break;
                    }
                }

                if (save.HasValue) { st.Pos = save.Value; st.Pen = penBefore; st.Scale = scaleBefore; }
            }
        }

        private static void Move(State st, List<ShapePrimitive> o, int dx, int dy)
        {
            var to = st.Pos + new Pt(dx, dy) * st.Scale;
            if (st.Pen && o != null) o.Add(new LinePrim(st.Pos, to));
            st.Pos = to;
        }

        /// <summary>Codes 10/11. spec = sign bit (clockwise) | start octant (bits 4-6) | octant count (bits 0-2, 0 = 8).</summary>
        private static void OctantArc(State st, List<ShapePrimitive> o, double r, int spec, int startOffset, int endOffset)
        {
            if (r <= 0) return;
            bool cw = (spec & 0x80) != 0;
            int s = (spec >> 4) & 7;
            int n = spec & 7;
            if (n == 0) n = 8;
            double sign = cw ? -1 : 1;
            double oct = Math.PI / 4;

            double start = s * oct + sign * startOffset * oct / 256;
            double sweep;
            if (startOffset == 0 && endOffset == 0) sweep = sign * n * oct;
            else
            {
                // Fractional arc: ends endOffset/256 of an octant past the boundary of the last octant spanned.
                double end = (s + sign * (n - 1)) * oct + sign * endOffset * oct / 256;
                sweep = end - start;
                if (endOffset == 0) sweep += sign * oct;
            }

            var center = st.Pos - new Pt(Math.Cos(start), Math.Sin(start)) * r;
            var arc = new ArcPrim(center, r, start, sweep);
            if (st.Pen && o != null) o.Add(arc);
            st.Pos = arc.End;
        }

        /// <summary>Codes 12/13: displacement plus bulge (-127..127; 127 = semicircle, positive = counter-clockwise).</summary>
        private static void Bulge(State st, List<ShapePrimitive> o, int dx, int dy, int bulge)
        {
            var to = st.Pos + new Pt(dx, dy) * st.Scale;
            if (st.Pen && o != null)
            {
                var arc = bulge == 0 ? null : ArcPrim.FromChord(st.Pos, to, 4 * Math.Atan(bulge / 127.0));
                o.Add(arc ?? (ShapePrimitive)new LinePrim(st.Pos, to));
            }
            st.Pos = to;
        }
    }
}
