using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TransactionLogging
{
    internal static class TransactionLoggingExtensions
    {
        public static string ToJsonString(this IEnumerable<Tuple<string, string, object, object>> entries)
        {
            //item1 is the entity name
            //item2 is the field name
            //item3 is old value
            //item4 is new value
            return new JArray(entries.Reverse().Select(entry => new JArray(entry.Item1 + "." + entry.Item2, entry.Item3, entry.Item4))).ToString(Newtonsoft.Json.Formatting.None);
        }

    }
}
