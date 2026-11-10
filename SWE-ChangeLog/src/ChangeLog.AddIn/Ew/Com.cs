using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using EwAPI;

namespace ChangeLog.AddIn.Ew
{
    /// <summary>Defensive helpers for calling the SWE COM API.</summary>
    internal static class Com
    {
        /// <summary>Runs a getter; COM failures yield the fallback instead of breaking the caller.</summary>
        public static T Try<T>(Func<T> getter, T fallback = default(T))
        {
            try
            {
                return getter();
            }
            catch (COMException)
            {
                return fallback;
            }
            catch (InvalidCastException)
            {
                return fallback;
            }
            catch (InvalidComObjectException)
            {
                return fallback;
            }
        }

        /// <summary>Enumerates the VARIANT arrays returned by get*Array methods.</summary>
        public static IEnumerable<T> Items<T>(object variantArray) where T : class
        {
            var e = variantArray as IEnumerable;
            if (e == null) yield break;
            foreach (var o in e)
            {
                var t = o as T;
                if (t != null) yield return t;
            }
        }

        public static void Check(EwErrorCode code, string operation)
        {
            if (code != EwErrorCode.EW_NO_ERROR)
                throw new SweApiException(operation + " failed: " + code, code);
        }

        /// <summary>COM type name of an object via IDispatch type info (e.g. "IEwProjectBookX").</summary>
        public static string TypeName(object com)
        {
            try
            {
                var d = com as IDispatchInfo;
                if (d == null) return null;
                var ti = d.GetTypeInfo(0, 0);
                if (ti == null) return null;
                string name, doc, help;
                int ctx;
                ti.GetDocumentation(-1, out name, out doc, out ctx, out help);
                return name;
            }
            catch (COMException)
            {
                return null;
            }
        }

        public static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o))
            {
                try { Marshal.ReleaseComObject(o); }
                catch (ArgumentException) { }
            }
        }
    }

    [ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDispatchInfo
    {
        int GetTypeInfoCount();

        [return: MarshalAs(UnmanagedType.Interface)]
        System.Runtime.InteropServices.ComTypes.ITypeInfo GetTypeInfo(int iTInfo, int lcid);
    }

    public sealed class SweApiException : Exception
    {
        public SweApiException(string message, EwErrorCode code) : base(message)
        {
            Code = code;
        }

        public EwErrorCode Code { get; }
    }
}
