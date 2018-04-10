using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TransactionLogging
{
    class SecurityContext
    {
        public string UserName { get; set; }

        //SAMPLE CODE
        //AppDomain
        //Application context should set current identity
        public static SecurityContext Current {
            get
            {
                return new SecurityContext() { UserName = "" };
            }
        }
    }
}
