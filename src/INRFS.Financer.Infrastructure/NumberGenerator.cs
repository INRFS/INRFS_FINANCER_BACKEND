namespace INRFS.Financer.Infrastructure;

internal static class NumberGenerator
{
    public static string New(string prefix) =>
        $"{prefix}-{DateTime.UtcNow:yyMMdd}-{Convert.ToHexString(Guid.NewGuid().ToByteArray())[..8]}";
}
