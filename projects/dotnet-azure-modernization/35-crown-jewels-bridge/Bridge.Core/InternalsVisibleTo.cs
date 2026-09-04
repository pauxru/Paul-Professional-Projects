using System.Runtime.CompilerServices;

// The report and the tests measure and assert things a consumer should never touch:
// the raw P/Invoke surface, without the SafeHandle above it. Exposing NativeMethods
// publicly to make that possible would put the unsafe path in the public API for the
// benefit of two internal callers. InternalsVisibleTo keeps the choice narrow.
[assembly: InternalsVisibleTo("Bridge.Report")]
[assembly: InternalsVisibleTo("Bridge.Tests")]
