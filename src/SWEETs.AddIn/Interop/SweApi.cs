using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EwAPI;

namespace SWEETs.AddIn.Interop
{
    public sealed class SweApiException : Exception
    {
        public SweApiException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Reflection-based bridge over the EwAPI interop assembly.
    ///
    /// Why: EwAPI member names occasionally change between releases. Every call names its interface plus one or
    /// more candidate member names (verified against the SWE 2026 type library, see <see cref="SweContract"/>);
    /// the first that exists in the referenced interop assembly is used. <see cref="CheckContract"/> lists any
    /// member that cannot be resolved, so a mismatch shows up as a clear message in the SWEETs log
    /// instead of a crash mid-build. Trailing "out EwErrorCode" parameters are appended automatically and
    /// checked, as are EwErrorCode return values.
    /// </summary>
    public static class SweApi
    {
        private static readonly Assembly Interop = typeof(EwErrorCode).Assembly;
        private static readonly Dictionary<string, MethodInfo> Cache = new Dictionary<string, MethodInfo>();
        private static readonly List<(string iface, string[] names)> Used = new List<(string, string[])>();

        public static T Get<T>(object target, string iface, params string[] names) => (T)Call(target, iface, names);
        public static object Get(object target, string iface, params string[] names) => Call(target, iface, names);

        public static object Call(object target, string iface, string[] names, params object[] args)
        {
            if (target == null) throw new SweApiException($"{iface}.{names[0]}: target object is null.");
            var m = Resolve(iface, names, args.Length);
            var ps = m.GetParameters();
            var callArgs = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (i < args.Length) callArgs[i] = Coerce(args[i], ps[i].ParameterType);
                else if (ps[i].ParameterType.IsByRef) callArgs[i] = null; // out EwErrorCode slot
                else throw new SweApiException($"{iface}.{m.Name}: expected {ps.Length} arguments, got {args.Length}.");
            }

            object result;
            try { result = m.Invoke(target, callArgs); }
            catch (Exception ex) when (IsInterfaceMismatch(ex))
            {
                // The COM object did not answer QueryInterface for this interface's IID (typically a base interface
                // such as IInteropFactoryX when SWE hands out IEwInteropFactoryX). Try the same member declared on
                // any other interop interface the object does implement, then IDispatch late binding.
                result = ViaOtherInterface(target, m, callArgs, out var ok);
                if (!ok) result = LateBound(target, m, callArgs);
            }
            catch (TargetInvocationException tie) { throw new SweApiException($"{iface}.{m.Name} failed: {tie.InnerException?.Message}", tie.InnerException); }

            for (int i = args.Length; i < ps.Length; i++)
                if (callArgs[i] is EwErrorCode outErr) Ensure(outErr, iface, m.Name);
            if (result is EwErrorCode code) Ensure(code, iface, m.Name);
            return result;
        }

        private static bool IsInterfaceMismatch(Exception ex) =>
            ex is InvalidCastException || ex is TargetException ||
            (ex is TargetInvocationException tie && tie.InnerException is InvalidCastException);

        private static object ViaOtherInterface(object target, MethodInfo m, object[] callArgs, out bool ok)
        {
            ok = false;
            var sig = m.GetParameters().Select(p => p.ParameterType).ToArray();
            foreach (var t in Interop.GetTypes().Where(t => t.IsInterface && t != m.DeclaringType && t.IsInstanceOfType(target)))
            {
                var alt = t.GetMethod(m.Name, sig);
                if (alt == null) continue;
                try { var r = alt.Invoke(target, callArgs); ok = true; return r; }
                catch (Exception ex) when (IsInterfaceMismatch(ex)) { }
            }
            return null;
        }

        private static object LateBound(object target, MethodInfo m, object[] callArgs)
        {
            var mods = new ParameterModifier(Math.Max(1, callArgs.Length));
            var ps = m.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                mods[i] = ps[i].ParameterType.IsByRef;
                // IDispatch rejects an empty VARIANT for a typed [out] parameter (DISP_E_TYPEMISMATCH): seed it.
                if (callArgs[i] == null && ps[i].ParameterType.IsByRef)
                {
                    var et = ps[i].ParameterType.GetElementType();
                    callArgs[i] = et.IsEnum ? System.Enum.ToObject(et, 0)
                                : et == typeof(string) ? (object)string.Empty
                                : et.IsValueType ? Activator.CreateInstance(et) : null;
                }
            }
            try
            {
                return target.GetType().InvokeMember(m.Name, BindingFlags.InvokeMethod, null, target, callArgs, new[] { mods }, null, null);
            }
            catch (TargetInvocationException tie) { throw new SweApiException($"{m.Name} (late bound) failed: {tie.InnerException?.Message}", tie.InnerException); }
            catch (Exception ex) { throw new SweApiException($"{m.Name} (late bound) failed: {ex.Message}", ex); }
        }

        /// <summary>Call variant that tolerates a missing member (optional features such as descriptions).</summary>
        public static bool TryCall(object target, string iface, string[] names, out object result, params object[] args)
        {
            result = null;
            try { result = Call(target, iface, names, args); return true; }
            catch (SweApiException) { return false; }
        }

        public static void Do(object target, string iface, string name, params object[] args) => Call(target, iface, new[] { name }, args);

        public static object Call(object target, SweContract.Member m, params object[] args) => Call(target, m.Iface, m.Names, args);
        public static T Call<T>(object target, SweContract.Member m, params object[] args) => (T)Call(target, m.Iface, m.Names, args);
        public static bool TryCall(object target, SweContract.Member m, out object result, params object[] args) => TryCall(target, m.Iface, m.Names, out result, args);

        private static void Ensure(EwErrorCode code, string iface, string member)
        {
            if (code.ToString() != "EW_NO_ERROR")
                throw new SweApiException($"{iface}.{member} returned {code}.");
        }

        private static MethodInfo Resolve(string iface, string[] names, int argCount)
        {
            var key = iface + "|" + string.Join(",", names) + "|" + argCount;
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
                if (!Used.Any(u => u.iface == iface && u.names.SequenceEqual(names))) Used.Add((iface, names));

                var type = FindInterface(iface) ?? throw new SweApiException($"Interface {iface} not found in {Interop.GetName().Name}.");
                var methods = AllMethods(type);
                foreach (var n in names)
                {
                    var m = methods.Where(x => x.Name == n)
                                   .OrderBy(x => Math.Abs(x.GetParameters().Count(p => !p.IsOut && !p.ParameterType.IsByRef) - argCount))
                                   .FirstOrDefault();
                    if (m != null) { Cache[key] = m; return m; }
                }
                throw new SweApiException($"{iface} has none of: {string.Join(", ", names)} — update the candidate names in SweContract for this SWE version.");
            }
        }

        private static Type FindInterface(string name)
        {
            var types = Interop.GetTypes();
            return types.FirstOrDefault(t => t.IsInterface && t.Name == name)
                ?? types.FirstOrDefault(t => t.IsInterface && t.Name == "I" + name)
                ?? types.FirstOrDefault(t => t.IsInterface && "I" + t.Name == name);
        }

        private static IEnumerable<MethodInfo> AllMethods(Type t) =>
            t.GetMethods().Concat(t.GetInterfaces().SelectMany(i => i.GetMethods()));

        private static object Coerce(object v, Type target)
        {
            if (v == null) return null;
            var t = target.IsByRef ? target.GetElementType() : target;
            if (t.IsInstanceOfType(v)) return v;
            if (t.IsEnum) return v is string s ? System.Enum.Parse(t, s) : System.Enum.ToObject(t, v);
            if (t == typeof(object)) return v;
            return Convert.ChangeType(v, t);
        }

        /// <summary>Enum value by name from the interop assembly (e.g. "EwFileType", "kFileFolio").</summary>
        public static object EnumValue(string enumType, params string[] valueNames)
        {
            var t = Interop.GetTypes().FirstOrDefault(x => x.IsEnum && x.Name == enumType)
                    ?? throw new SweApiException($"Enum {enumType} not found.");
            foreach (var v in valueNames)
                if (System.Enum.GetNames(t).Contains(v)) return System.Enum.Parse(t, v);
            throw new SweApiException($"{enumType} has none of: {string.Join(", ", valueNames)}.");
        }

        /// <summary>
        /// Resolves every member this add-in relies on against the installed interop assembly.
        /// Returns human-readable problems; empty means fully compatible.
        /// </summary>
        public static List<string> CheckContract()
        {
            var problems = new List<string>();
            foreach (var (iface, names, optional) in SweContract.Members)
            {
                var type = FindInterface(iface);
                if (type == null) { problems.Add($"{(optional ? "optional" : "REQUIRED")}: interface {iface} missing"); continue; }
                var methods = new HashSet<string>(AllMethods(type).Select(m => m.Name));
                if (!names.Any(methods.Contains))
                    problems.Add($"{(optional ? "optional" : "REQUIRED")}: {iface}.{string.Join(" | ", names)} not found");
            }
            return problems;
        }

        public static string InteropDescription => $"{Interop.GetName().Name} {Interop.GetName().Version} ({Interop.Location})";
    }
}
