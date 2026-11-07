namespace Regions.Engine
{
    internal static class Num
    {
        // double.IsFinite does not exist in netstandard2.0.
        public static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        public static bool IsFiniteNonNegative(double x) => IsFinite(x) && x >= 0;

        public static bool IsFiniteNonNegative(double? x) => x == null || IsFiniteNonNegative(x.Value);
    }
}
