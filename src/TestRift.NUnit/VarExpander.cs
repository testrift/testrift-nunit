using System;
using System.Text.RegularExpressions;

namespace TestRift.NUnit
{
    public static class VarExpander
    {
        private static readonly Regex VarRegex = new(@"\$\{env:(?<name>[A-Za-z0-9_]+)\}");

        public static string Expand(string input)
        {
            return VarRegex.Replace(input, match =>
            {
                var name = match.Groups["name"].Value;
                var value = Environment.GetEnvironmentVariable(name);
                if (value == null)
                    throw new InvalidOperationException($"Required environment variable '{name}' is not set.");
                return value;
            });
        }
    }
}
