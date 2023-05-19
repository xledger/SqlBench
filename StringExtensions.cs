using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SqlBench {
    internal static class StringExtensions {
        public static string StringJoin(this IEnumerable<object> @this, string sep) {
            return string.Join(sep, @this);
        }
    }
}
