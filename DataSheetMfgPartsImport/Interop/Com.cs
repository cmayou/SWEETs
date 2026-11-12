using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DataSheetMfgPartsImport.Interop;

/// <summary>Small helpers for the SOLIDWORKS Electrical COM API.</summary>
internal static class Com
{
    public static bool Ok(EwErrorCode e) => e == EwErrorCode.EW_NO_ERROR;

    /// <summary>Throws when a mandatory call fails.</summary>
    public static void Require(EwErrorCode e, string what)
    {
        if (!Ok(e)) throw new EwApiException(what, e);
    }

    public static T Require<T>(T value, EwErrorCode e, string what) where T : class
    {
        if (!Ok(e) || value == null) throw new EwApiException(what, e);
        return value;
    }

    /// <summary>Late-bound call through IDispatch; byRef marks [out] parameters.</summary>
    public static object Invoke(object target, string method, object[] args, bool[] byRef = null)
    {
        ParameterModifier[] mods = null;
        if (byRef != null && byRef.Length > 0)
        {
            var pm = new ParameterModifier(byRef.Length);
            for (var i = 0; i < byRef.Length; i++) pm[i] = byRef[i];
            mods = new[] { pm };
        }
        return target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args, mods, null, null);
    }

    /// <summary>Calls "result method(inArgs..., out EwErrorCode)" late-bound. Returns null if the call fails.</summary>
    public static object CallWithError(object target, string method, params object[] inArgs)
    {
        try
        {
            var args = new object[inArgs.Length + 1];
            Array.Copy(inArgs, args, inArgs.Length);
            args[inArgs.Length] = 0;
            var byRef = new bool[args.Length];
            byRef[args.Length - 1] = true;
            var result = Invoke(target, method, args, byRef);
            var code = args[args.Length - 1] is int i ? i : 0;
            return code == 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Calls "int method()" late-bound (no error code), 0 on failure.</summary>
    public static int CallInt(object target, string method)
    {
        try { return Convert.ToInt32(Invoke(target, method, new object[0])); }
        catch { return 0; }
    }

    public static void Release(object o)
    {
        try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
        catch { /* ignore */ }
    }
}

public sealed class EwApiException : Exception
{
    public EwErrorCode Code { get; }

    public EwApiException(string what, EwErrorCode code)
        : base($"{what} failed ({code})") => Code = code;
}
