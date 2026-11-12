using System.Collections.Generic;
using ChangeLog.Core.Model;
using EwAPI;

namespace ChangeLog.AddIn.Ew
{
    /// <summary>
    /// Converts SWE COM objects into plain <see cref="ObjectState"/>. This is the ONLY place
    /// that knows which SWE properties are tracked: to log a new property, add one Set(...) line.
    /// Field names are user-facing (they appear in the log and the Excel export).
    /// </summary>
    public sealed class EwObjectReader
    {
        private readonly IEwProjectX _project;
        private readonly string _lang;

        /// <summary>Component id → tag, so symbol/part labels need no extra COM call per object.</summary>
        private readonly Dictionary<int, string> _componentTags = new Dictionary<int, string>();

        public EwObjectReader(IEwProjectX project, string language)
        {
            _project = project;
            _lang = language ?? string.Empty;
        }

        public ObjectState Read(object com)
        {
            if (com == null) return null;
            EwErrorCode e;

            // Order matters: most specific interfaces first.
            var page = com as IEwProjectFileX;
            if (page != null)
            {
                var tag = Com.Try(() => page.getTag(out e));
                var desc = Com.Try(() => page.getDescription(_lang, out e));
                return new ObjectState
                    {
                        Kind = ObjectKind.Page, Id = page.getID(), PageId = null,
                        Label = string.IsNullOrEmpty(desc) ? tag : tag + " – " + desc
                    }
                    .Set("Page", tag)
                    .Set("Description", desc)
                    .Set("Type", Com.Try(() => page.getFileType().ToString().Replace("kFile", string.Empty)))
                    .Set("Position", Com.Try(() => page.getPosition(out e)))
                    .Set("Location ID", Com.Try(() => page.getLocationID(out e)))
                    .Set("Function ID", Com.Try(() => page.getFunctionID(out e)));
            }

            var sym = com as IEwProjectSymbolX;
            if (sym != null)
            {
                var compId = Com.Try(() => sym.getObjectID(out e));
                var comp = ComponentTag(compId);
                var name = Com.Try(() => sym.getEwSymbolName(out e));
                var x = Com.Try(() => sym.getXPosition(out e));
                var y = Com.Try(() => sym.getYPosition(out e));
                var w = Com.Try(() => sym.getWidth(out e));
                var h = Com.Try(() => sym.getHeight(out e));
                var rot = Com.Try(() => sym.getRotationAngle(out e));
                return new ObjectState
                    {
                        Kind = ObjectKind.Symbol, Id = sym.getID(), PageId = NullIfZero(Com.Try(() => sym.getFileID(out e))),
                        Label = string.IsNullOrEmpty(comp) ? name : comp + " (" + name + ")",
                        Geometry = SymbolGeometry(x, y, w, h, rot)
                    }
                    .Set("Symbol", name)
                    .Set("Symbol type", Com.Try(() => sym.getEwSymbolType(out e).ToString().Replace("kSymbol", string.Empty)))
                    .Set("Component", comp)
                    .Set("Manufacturer part ID", Com.Try(() => sym.getManufacturerPartID(out e)))
                    .Set("Location ID", Com.Try(() => sym.getLocationID(out e)))
                    .Set("Function ID", Com.Try(() => sym.getFunctionID(out e)));
            }

            var line = com as IEwProjectLineX;
            if (line != null)
            {
                var mark = Com.Try(() => line.getWireMarkText(out e));
                return new ObjectState
                    {
                        Kind = ObjectKind.WireSegment, Id = line.getID(), PageId = NullIfZero(Com.Try(() => line.getFileID(out e))),
                        Label = string.IsNullOrEmpty(mark) ? "Wire segment" : "Wire segment " + mark,
                        Geometry = Geometry.Segment(
                            Com.Try(() => line.getStartPointXPosition(out e)), Com.Try(() => line.getStartPointYPosition(out e)),
                            Com.Try(() => line.getEndPointXPosition(out e)), Com.Try(() => line.getEndPointYPosition(out e)))
                    }
                    .Set("Wire mark", mark)
                    .Set("Line type", Com.Try(() => line.getEwLineType(out e).ToString().Replace("kLine", string.Empty)))
                    .Set("Equipotential ID", Com.Try(() => line.getEquipotentialID(out e)));
            }

            var wire = com as IEwProjectWireX;
            if (wire != null)
            {
                var tag = Com.Try(() => wire.getTag(out e));
                return new ObjectState { Kind = ObjectKind.Wire, Id = wire.getID(), Label = string.IsNullOrEmpty(tag) ? null : "Wire " + tag }
                    .Set("Tag", tag)
                    .Set("Section/gauge", Com.Try(() => wire.getSectionOrGauge(out e)))
                    .Set("Color", Com.Try(() => wire.getColorCode(out e)))
                    .Set("Signal", Com.Try(() => wire.getSignal(out e)))
                    .Set("Equipotential", Com.Try(() => wire.getEquipotential(out e)))
                    .Set("Origin", Com.Try(() => wire.getOrigin(out e)))
                    .Set("Destination", Com.Try(() => wire.getDestination(out e)))
                    .Set("Origin component", ComponentTag(Com.Try(() => wire.getOriginComponentID(out e))))
                    .Set("Destination component", ComponentTag(Com.Try(() => wire.getDestinationComponentID(out e))))
                    .Set("Cable ID", Com.Try(() => wire.getCableID(out e)))
                    .Set("Length", Com.Try(() => wire.getLength(out e)));
            }

            var cable = com as IEwProjectCableX;
            if (cable != null)
            {
                var tag = Com.Try(() => cable.getTag(out e));
                return new ObjectState { Kind = ObjectKind.Cable, Id = cable.getID(), Label = tag }
                    .Set("Tag", tag)
                    .Set("Reference", Com.Try(() => cable.getReference(out e)))
                    .Set("Manufacturer", Com.Try(() => cable.getManufacturer(out e)))
                    .Set("Article number", Com.Try(() => cable.getArticleNumber(out e)))
                    .Set("Cores", Com.Try(() => cable.getCoreCount(out e)))
                    .Set("Length", Com.Try(() => cable.getLength(out e)))
                    .Set("Color", Com.Try(() => cable.getColorCode(out e)))
                    .Set("Description", Com.Try(() => cable.getDescription(_lang, out e)));
            }

            var part = com as IEwProjectManufacturerPartX;
            if (part != null)
            {
                var reference = Com.Try(() => part.getReference(out e));
                var compTag = Com.Try(() => part.getEwProjectComponent(out e)?.getTag(out e));
                return new ObjectState
                    {
                        Kind = ObjectKind.ManufacturerPart, Id = part.getID(),
                        Label = string.IsNullOrEmpty(compTag) ? reference : reference + " → " + compTag
                    }
                    .Set("Reference", reference)
                    .Set("Manufacturer", Com.Try(() => part.getManufacturer(out e)))
                    .Set("Article number", Com.Try(() => part.getArticleNumber(out e)))
                    .Set("Description", Com.Try(() => part.getDescription(_lang, out e)))
                    .Set("Component", compTag);
            }

            var text = com as IEwProjectMultilingualTextX;
            if (text != null)
            {
                var content = Com.Try(() => text.getDescription(_lang, out e));
                return new ObjectState
                    {
                        Kind = ObjectKind.Text, Id = text.getID(), PageId = NullIfZero(Com.Try(() => text.getFileID(out e))),
                        Label = Truncate(content, 40),
                        Geometry = Geometry.Point(Com.Try(() => text.getXPosition(out e)), Com.Try(() => text.getYPosition(out e)), Com.Try(() => text.getRotationAngle(out e)))
                    }
                    .Set("Text", content);
            }

            var harness = com as IEwProjectHarnessX;
            if (harness != null)
                return Tagged(ObjectKind.Harness, harness, Com.Try(() => harness.getDescription(_lang, out e)));

            var loc = com as IEwProjectLocationX;
            if (loc != null)
                return Tagged(ObjectKind.Location, loc, Com.Try(() => loc.getDescription(_lang, out e))).Set("Path", Com.Try(() => loc.getTagPath(out e)));

            var fn = com as IEwProjectFunctionX;
            if (fn != null)
                return Tagged(ObjectKind.Function, fn, Com.Try(() => fn.getDescription(_lang, out e))).Set("Path", Com.Try(() => fn.getTagPath(out e)));

            var compX = com as IEwProjectComponentX;
            if (compX != null)
            {
                var s = Tagged(ObjectKind.Component, compX, Com.Try(() => compX.getDescription(_lang, out e)))
                    .Set("Path", Com.Try(() => compX.getTagPath(out e)))
                    .Set("Type", Com.Try(() => compX.getType(out e).ToString()))
                    .Set("Location ID", Com.Try(() => compX.getLocationID(out e)))
                    .Set("Function ID", Com.Try(() => compX.getFunctionID(out e)))
                    .Set("Parent ID", Com.Try(() => compX.getParentID(out e)));
                _componentTags[s.Id] = s.Get("Tag");
                return s;
            }

            var project = com as IEwProjectX;
            if (project != null)
                return new ObjectState { Kind = ObjectKind.Project, Id = project.getID(), Label = Com.Try(() => project.getName(out e)) }
                    .Set("Name", Com.Try(() => project.getName(out e)));

            var obj = com as IEwObjectX;
            if (obj == null) return null;
            return new ObjectState { Kind = ObjectKind.Other, Id = Com.Try(() => obj.getID()), TypeName = Com.TypeName(com), Label = Com.TypeName(com) };
        }

        /// <summary>
        /// Identity only (kind + id), without reading properties. Used for delete events, where
        /// the object may already be gone from the database; label and page come from the cache.
        /// </summary>
        public bool TryIdentify(object com, out ObjectKey key)
        {
            key = default(ObjectKey);
            if (com == null) return false;
            ObjectKind kind;
            if (com is IEwProjectFileX) kind = ObjectKind.Page;
            else if (com is IEwProjectSymbolX) kind = ObjectKind.Symbol;
            else if (com is IEwProjectLineX) kind = ObjectKind.WireSegment;
            else if (com is IEwProjectWireX) kind = ObjectKind.Wire;
            else if (com is IEwProjectCableX) kind = ObjectKind.Cable;
            else if (com is IEwProjectManufacturerPartX) kind = ObjectKind.ManufacturerPart;
            else if (com is IEwProjectMultilingualTextX) kind = ObjectKind.Text;
            else if (com is IEwProjectHarnessX) kind = ObjectKind.Harness;
            else if (com is IEwProjectLocationX) kind = ObjectKind.Location;
            else if (com is IEwProjectFunctionX) kind = ObjectKind.Function;
            else if (com is IEwProjectComponentX) kind = ObjectKind.Component;
            else if (com is IEwProjectX)
            {
                var p = (IEwProjectX)com;
                key = new ObjectKey(ObjectKind.Project, Com.Try(() => p.getID(), -1));
                return key.Id > 0;
            }
            else kind = ObjectKind.Other;

            var o = com as IEwObjectX;
            var id = o == null ? -1 : Com.Try(() => o.getID(), -1);
            key = new ObjectKey(kind, id);
            return id > 0;
        }

        public void RememberComponent(int id, string tag) => _componentTags[id] = tag;

        private ObjectState Tagged(ObjectKind kind, IEwProjectTagObjectX o, string description)
        {
            EwErrorCode e;
            var tag = Com.Try(() => o.getTag(out e));
            return new ObjectState { Kind = kind, Id = o.getID(), Label = tag }.Set("Tag", tag).Set("Description", description);
        }

        private string ComponentTag(int id)
        {
            if (id <= 0) return null;
            string tag;
            if (_componentTags.TryGetValue(id, out tag)) return tag;
            EwErrorCode e;
            tag = Com.Try(() => _project.getEwProjectComponentManager(out e).findEwProjectComponentByID(id, out e)?.getTag(out e));
            _componentTags[id] = tag;
            return tag;
        }

        /// <summary>
        /// SWE reports the symbol insertion point plus width/height. The insertion point is not
        /// guaranteed to be the bounding-box corner for every symbol; we treat it as the
        /// bottom-left corner. If overlays look offset on real projects, adjust ONLY here.
        /// </summary>
        private static Geometry SymbolGeometry(double x, double y, double w, double h, double rotation)
        {
            if (w <= 0 || h <= 0) return Geometry.Point(x, y, rotation);
            return Geometry.Rect(x, y, w, h, rotation);
        }

        private static int? NullIfZero(int v) => v > 0 ? v : (int?)null;

        private static string Truncate(string s, int n) => s == null || s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    }
}
