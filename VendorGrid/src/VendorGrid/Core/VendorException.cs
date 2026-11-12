using System;

namespace VendorGrid.Core
{
    /// <summary>Expected provider failure with a message that is safe to show to the user.</summary>
    public sealed class VendorException : Exception
    {
        public VendorException(string message) : base(message) { }
    }
}
