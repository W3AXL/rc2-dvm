using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using fnecore.Utility;

namespace rc2_dvm
{
    internal class AssemblyVersion
    {
        private static DateTime creationDate = new DateTime(2012, 5, 6);

        /// <summary>Name of the assembly</summary>
        public static string _NAME;

        /// <summary>Constructed full version string.</summary>
        public static string _VERSION;

        /// <summary>Version of the assembly</summary>
        public static SemVersion _SEM_VERSION;

        /// <summary>Build date of the assembly.</summary>
        public static string _BUILD_DATE;

        /// <summary>Copyright string contained within the assembly.</summary>
        public static string _COPYRIGHT;

        /// <summary>Company string contained within the assembly.</summary>
        public static string _COMPANY;

        /*
        ** Methods
        */

        /// <summary>
        /// Initializes static members of the <see cref="AssemblyVersion"/> class.
        /// </summary>
        static AssemblyVersion()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
#if DEBUG
            _SEM_VERSION = new SemVersion(asm, "DEBUG_DNR");
#else
            _SEM_VERSION = new SemVersion(asm);
#endif

            AssemblyProductAttribute asmProd = asm.GetCustomAttributes(typeof(AssemblyProductAttribute), false)[0] as AssemblyProductAttribute;
            AssemblyCopyrightAttribute asmCopyright = asm.GetCustomAttributes(typeof(AssemblyCopyrightAttribute), false)[0] as AssemblyCopyrightAttribute;
            AssemblyCompanyAttribute asmCompany = asm.GetCustomAttributes(typeof(AssemblyCompanyAttribute), false)[0] as AssemblyCompanyAttribute;

            DateTime buildDate = new DateTime(2000, 1, 1).AddDays(asm.GetName().Version.Build).AddSeconds(asm.GetName().Version.Revision * 2);
            TimeSpan dateDifference = buildDate - creationDate;

            int totalMonths = (int)Math.Round(Math.Round(dateDifference.TotalDays, MidpointRounding.AwayFromZero) / 12, MidpointRounding.AwayFromZero) + 1;

            _NAME = asmProd.Product;

            _BUILD_DATE = buildDate.ToShortDateString() + " at " + buildDate.ToShortTimeString();

            _COPYRIGHT = asmCopyright.Copyright;
            _COMPANY = asmCompany.Company;

            _VERSION = $"{_NAME} {_SEM_VERSION.ToString()} (Built: {_BUILD_DATE})";
        }
    }
}
