namespace HsbgCardLookup.Search
{
    /// <summary>Port of extensions/shared/src/search/levenshtein.ts.</summary>
    internal static class Levenshtein
    {
        public static int Distance(string a, string b)
        {
            int m = a.Length, n = b.Length;
            var dp = new int[m + 1, n + 1];

            for (int i = 0; i <= m; i++) dp[i, 0] = i;
            for (int j = 0; j <= n; j++) dp[0, j] = j;

            for (int i = 1; i <= m; i++)
            {
                for (int j = 1; j <= n; j++)
                {
                    dp[i, j] = a[i - 1] == b[j - 1]
                        ? dp[i - 1, j - 1]
                        : 1 + System.Math.Min(dp[i - 1, j], System.Math.Min(dp[i, j - 1], dp[i - 1, j - 1]));
                }
            }

            return dp[m, n];
        }
    }
}
