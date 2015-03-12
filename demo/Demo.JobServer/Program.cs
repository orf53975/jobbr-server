﻿using System;

using Jobbr.Server;
using Jobbr.Server.Common;
using Jobbr.Server.Dapper;

namespace Demo.JobServer
{
    /// <summary>
    /// The program.
    /// </summary>
    public class Program
    {
        /// <summary>
        /// The main.
        /// </summary>
        /// <param name="args">
        /// The args.
        /// </param>
        public static void Main(string[] args)
        {
            var storageProvider = new DapperStorageProvider(@"Data Source=.\SQLEXPRESS;Initial Catalog=JobbrDemo;Integrated Security=True");

            var config = new DefaultJobbrConfiguration
                             {
                                 JobStorageProvider = storageProvider,
                                 ArtefactStorageProvider = new FileSystemArtefactsStorageProvider("data"),
                                 JobRunnerExeResolver = () => @"..\..\..\Demo.JobRunner\bin\Debug\Demo.JobRunner.exe",
                                 BeChatty = true,
                             };

            using (var jobbrServer = new JobbrServer(config))
            {
                jobbrServer.Start();

                Console.ReadLine();

                jobbrServer.Stop();
            }
        }
    }
}
