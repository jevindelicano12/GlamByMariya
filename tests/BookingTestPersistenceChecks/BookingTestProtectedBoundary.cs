// Fail closed until reviewed database operations replace direct table access and
// proof logging is controlled. No environment switch can activate this gate.
internal static class BookingTestProtectedBoundary
{
    internal static void RequireImplemented() =>
        throw new InvalidOperationException("Protected test booking database operations are not implemented.");
}
