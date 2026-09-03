using System;

namespace DeployToSolution.Models
{
    /// <summary>One entry of the solutioncomponent.componenttype option set, as read from the environment.</summary>
    public class ComponentTypeDef
    {
        public int Value { get; set; }
        public string Label { get; set; }
        /// <summary>Label lowercased with every non-alphanumeric character removed ("SDK Message Processing Step" -> "sdkmessageprocessingstep").</summary>
        public string Key { get; set; }

        public static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            Span<char> buf = stackalloc char[s.Length];
            int n = 0;
            foreach (var c in s)
                if (char.IsLetterOrDigit(c)) buf[n++] = char.ToLowerInvariant(c);
            return new string(buf.Slice(0, n));
        }

        public override string ToString() => Label;
    }
}
