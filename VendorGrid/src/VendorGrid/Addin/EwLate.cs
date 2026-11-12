using System;
using System.Reflection;
using EwAPI;

namespace VendorGrid.Addin
{
    /// <summary>
    /// Late-bound (IDispatch) calls into SOLIDWORKS Electrical objects. The Electrical API interfaces are dual, so this works even
    /// when the host hands out objects that cannot be cast to the typed interop interfaces.
    /// Every Electrical method that can fail ends in "out EwErrorCode errorCode"; <see cref="CallOut"/> supplies that argument.
    /// </summary>
    internal static class EwLate
    {
        static readonly int NoError = (int)EwErrorCode.EW_NO_ERROR;

        public static bool Ok(int errorCode) { return errorCode == NoError; }

        /// <summary>Calls a method that has no error-code parameter (or whose error code is its return value).</summary>
        public static object Call(object target, string method, params object[] args)
        {
            return Invoke(target, method, args, null);
        }

        /// <summary>Calls a method whose LAST parameter is "out EwErrorCode errorCode".</summary>
        public static object CallOut(object target, string method, out int errorCode, params object[] args)
        {
            var all = new object[args.Length + 1];
            Array.Copy(args, all, args.Length);
            all[args.Length] = 0;
            var modifiers = new ParameterModifier(all.Length);
            modifiers[args.Length] = true;
            var result = Invoke(target, method, all, new[] { modifiers });
            errorCode = Convert.ToInt32(all[args.Length]);
            return result;
        }

        static object Invoke(object target, string method, object[] args, ParameterModifier[] modifiers)
        {
            if (target == null) throw new ArgumentNullException("target", "Electrical object is not available (" + method + ").");
            try
            {
                return target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args, modifiers, null, null);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        public static string Str(object value)
        {
            return value == null ? "" : Convert.ToString(value).Trim();
        }
    }
}
