using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TestLibrary;
using Newtonsoft.Json;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;

using CmisSync.Lib;
using CmisSync.Lib.Sync;
using CmisSync.Lib.Cmis;

using log4net;
using log4net.Config;

// Useful to debug unit tests.
namespace TestLibraryRunner
{
    class TrustAlways : ICertificatePolicy
    {
        public bool CheckValidationResult (ServicePoint sp, X509Certificate certificate, WebRequest request, int error)
        {
            // For testing, always accept any certificate
            return true;
        }

    }

    class Program
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(Program));
        static int serverId = 0; // Which server in the JSON file (first=0)

        static void test(string path)
        {
            IEnumerable<object[]> servers = JsonConvert.DeserializeObject<List<object[]>>(
                    File.ReadAllText(path));
            object[] server = servers.ElementAt(serverId);

            CmisSyncTests tests = new CmisSyncTests();

            tests.Init();
            tests.Sync((string)server[0], (string)server[1],
                    (string)server[2], (string)server[3], (string)server[4], (string)server[5], (string)server[6]);
            tests.TearDown();
        }

        static void testFuzzy()
        {
            IEnumerable<object[]> servers = JsonConvert.DeserializeObject<List<object[]>>(
                    File.ReadAllText("../../../TestLibrary/test-servers-fuzzy.json"));
            object[] server = servers.ElementAt(serverId);
            new CmisSyncTests().GetRepositoriesFuzzy((string)server[0], (string)server[1], (string)server[2]);
        }

        static void Main(string[] args)
        {
            ServicePointManager.CertificatePolicy = new TrustAlways();
            bool firstRun = ! File.Exists(ConfigManager.CurrentConfigFile);

            // Migrate config.xml from past versions, if necessary.
            if ( ! firstRun )
                ConfigMigration.Migrate();

            string path = null;

            foreach (string arg in args)
            {
                if (File.Exists(arg))
                {
                    path = arg;
                    break;
                }
            }

            FileInfo alternativeLog4NetConfigFile = new FileInfo(Path.Combine(Directory.GetParent(ConfigManager.CurrentConfigFile).FullName, "log4net.config"));
            if(alternativeLog4NetConfigFile.Exists)
            {
                log4net.Config.XmlConfigurator.ConfigureAndWatch(alternativeLog4NetConfigFile);
            }
            else
            {
                log4net.Config.XmlConfigurator.Configure(ConfigManager.CurrentConfig.GetLog4NetConfig());
            }

            //new CmisSyncTests().TestCrypto();
            test(path == null ? "../../../TestLibrary/test-servers.json" : path);
            //testFuzzy();
        }
    }
}
