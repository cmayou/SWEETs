using System.Collections.Generic;
using ChangeLog.Core.Model;
using EwAPI;

namespace ChangeLog.AddIn.Ew
{
    /// <summary>
    /// Enumerates a whole project as <see cref="ObjectState"/>s. It is an iterator so the
    /// caller can process it in small slices on the SWE UI thread (COM objects are bound to
    /// that STA thread; reading them from a worker would marshal every call back anyway).
    /// </summary>
    public sealed class StateCollector
    {
        private readonly IEwProjectX _project;
        private readonly EwObjectReader _reader;

        public StateCollector(IEwProjectX project, EwObjectReader reader)
        {
            _project = project;
            _reader = reader;
        }

        public IEnumerable<ObjectState> Collect()
        {
            EwErrorCode e;

            // Components first: symbol and part labels resolve their tags from this cache.
            var components = Com.Try(() => _project.getEwProjectComponentManager(out e));
            if (components != null)
                foreach (var c in Com.Items<IEwProjectComponentX>(Com.Try<object>(() => components.getEwProjectComponentArray(out e))))
                    yield return Read(c);

            var pages = new List<IEwProjectFileX>();
            var files = Com.Try(() => _project.getEwProjectFileManager(out e));
            if (files != null)
                foreach (var f in Com.Items<IEwProjectFileX>(Com.Try<object>(() => files.getEwProjectFileArray(out e))))
                {
                    pages.Add(f);
                    yield return Com.Try(() => _reader.Read(f)); // released after the symbol pass below
                }

            var symbols = Com.Try(() => _project.getEwProjectSymbolManager(out e));
            foreach (var p in pages)
            {
                var id = Com.Try(() => p.getID());
                Com.Release(p);
                if (symbols == null || id <= 0) continue;
                foreach (var s in Com.Items<IEwProjectSymbolX>(Com.Try<object>(() => symbols.getProjectSymbolsFromFileID(id, out e))))
                    yield return Read(s);
            }

            var lines = Com.Try(() => _project.getEwProjectLineManager(out e));
            if (lines != null)
                foreach (var l in Com.Items<IEwProjectLineX>(Com.Try<object>(() => lines.getEwProjectLineArray(out e))))
                    yield return Read(l);

            var wires = Com.Try(() => _project.getEwProjectWireManager(out e));
            if (wires != null)
                foreach (var w in Com.Items<IEwProjectWireX>(Com.Try<object>(() => wires.getEwProjectWireArray(out e))))
                    yield return Read(w);

            var cables = Com.Try(() => _project.getEwProjectCableManager(out e));
            if (cables != null)
                foreach (var c in Com.Items<IEwProjectCableX>(Com.Try<object>(() => cables.getEwProjectCableArray(out e))))
                    yield return Read(c);

            var parts = Com.Try(() => _project.getEwProjectManufacturerPartManager(out e));
            if (parts != null)
                foreach (var m in Com.Items<IEwProjectManufacturerPartX>(Com.Try<object>(() => parts.getEwProjectManufacturerPartArray(out e))))
                    yield return Read(m);

            var texts = Com.Try(() => _project.getEwProjectMultilingualTextManager(out e));
            if (texts != null)
                foreach (var t in Com.Items<IEwProjectMultilingualTextX>(Com.Try<object>(() => texts.getEwProjectMultilingualTextArray(out e))))
                    yield return Read(t);
        }

        private ObjectState Read(object com)
        {
            var s = Com.Try(() => _reader.Read(com));
            Com.Release(com);
            return s;
        }
    }
}
