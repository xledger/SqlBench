using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SqlBench {
    internal class UserCausedException : Exception {
        public List<string> UserErrors = new List<string>();

        public UserCausedException(string message, IReadOnlyList<string> errors) : base(message) {
            UserErrors.AddRange(errors);
        }
    }
}
