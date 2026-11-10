using System.Runtime.InteropServices;

// Keep every type out of COM. Inside SWEETs nothing in VendorGrid is COM-registered; SearchCommand is [ComVisible(true)]
// only so SOLIDWORKS Electrical can call it through the command manager.
[assembly: ComVisible(false)]
