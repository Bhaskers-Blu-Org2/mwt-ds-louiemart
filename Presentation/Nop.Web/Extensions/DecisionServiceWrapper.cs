﻿using ClientDecisionService;
using Microsoft.AspNet.SignalR;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using MultiWorldTesting;
using Newtonsoft.Json;
using Nop.Core.Caching;
using Nop.Web.Controllers;
using Nop.Web.Hubs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Hosting;

namespace Nop.Web.Extensions
{
    public static class DecisionServiceWrapper<TContext>
    {
        static readonly string appToken = "10198550-a074-4f9c-8b15-cc389bc2bbbe";
        static readonly string commandCenterAddress = "http://mwtds.azurewebsites.net";
        static readonly string settingsFile = HostingEnvironment.MapPath("~/settings.json");

        public static EpsilonGreedyExplorer<TContext> Explorer { get; set; }
        public static DecisionServiceConfiguration<TContext> Configuration { get; set; }
        public static DecisionService<TContext> Service { get; set; }
        public static DateTimeOffset LastBlobModifiedDate { get; set; }

        public static void Create(uint numActions, string modelOutputDir, int policyAction)
        {
            if (Explorer == null)
            {
                Explorer = new EpsilonGreedyExplorer<TContext>(new MartPolicy<TContext>(policyAction), LoadSettings().Epsilon, numActions);
            }

            if (Configuration == null)
            {
                Configuration = new DecisionServiceConfiguration<TContext>(appToken, Explorer)
                {
                    BlobOutputDir = modelOutputDir,
                    BatchConfig = new BatchingConfiguration 
                    {
                        MaxDuration = TimeSpan.FromSeconds(2),
                        MaxBufferSizeInBytes = 1024,
                        MaxEventCount = 100,
                        MaxUploadQueueCapacity = 4,
                        UploadRetryPolicy = BatchUploadRetryPolicy.Retry
                    }
                };
            }

            if (Service == null)
            {
                Service = new DecisionService<TContext>(Configuration);
            }

            if (!File.Exists(settingsFile))
            {
                File.WriteAllText(settingsFile, JsonConvert.SerializeObject(new DecisionServiceSettings()));
            }
        }

        public static void ObserveStorageAndRetrain(CancellationToken cancelToken, int numberOfActions)
        {
            bool retrainOnUpdate = true;

            CloudStorageAccount storageAccount = null;
            CloudBlobClient blobClient = null;
            try
            {
                using (var wc = new WebClient())
                {
                    string jsonMetadata = wc.DownloadString(commandCenterAddress + "/Application/GetMetadata?token=" + appToken);
                    ApplicationTransferMetadata appMetadata = JsonConvert.DeserializeObject<ApplicationTransferMetadata>(jsonMetadata);

                    storageAccount = CloudStorageAccount.Parse(appMetadata.ConnectionString);
                    blobClient = storageAccount.CreateCloudBlobClient();
                    
                }
            }
            catch { }

            DecisionServiceSettings settings = LoadSettings();
            int serverObserveDelay = settings.ServerObserveDelay;
            int modelRetrainPeriodicDelay = settings.ModelRetrainPeriodicDelay;

            if (storageAccount == null || blobClient == null)
            {
                retrainOnUpdate = false;
                Trace.TraceWarning("Could not connect to Azure Storage for observation, Model Retraining will run automatically every {0} ms.", modelRetrainPeriodicDelay);
            }

            int waitCount = 0;

            if (LastBlobModifiedDate == null)
            {
                LastBlobModifiedDate = new DateTimeOffset();
            }

            while (!cancelToken.IsCancellationRequested)
            {
                cancelToken.WaitHandle.WaitOne(serverObserveDelay);
                waitCount++;

                if (retrainOnUpdate)
                {
                    IEnumerable<CloudBlobContainer> containers = blobClient.ListContainers("complete");

                    var lastContainerDate = new DateTimeOffset();
                    CloudBlobContainer lastContainer = null;
                    foreach (var container in containers)
                    {
                        if (container.Properties.LastModified.Value >= lastContainerDate)
                        {
                            lastContainerDate = container.Properties.LastModified.Value;
                            lastContainer = container;
                        }
                    }
                    if (lastContainer != null)
                    {
                        var lastBlobDate = new DateTimeOffset();
                        IEnumerable<IListBlobItem> blobs = lastContainer.ListBlobs();
                        foreach (var blob in blobs)
                        {
                            if (blob is CloudBlockBlob)
                            {
                                DateTimeOffset blobDate = ((CloudBlockBlob)blob).Properties.LastModified.Value;
                                if (blobDate >= lastBlobDate)
                                {
                                    lastBlobDate = blobDate;
                                }
                            }
                        }
                        if (lastBlobDate > LastBlobModifiedDate)
                        {
                            LastBlobModifiedDate = lastBlobDate;
                            Trace.WriteLine(TraceMessage.GetHeader(TraceMessage.TraceComponentType.Server) + "new data created.");

                            AutoRetrainModel(numberOfActions);
                        }
                    }
                }
                else
                {
                    if (waitCount >= ((float)modelRetrainPeriodicDelay / serverObserveDelay))
                    {
                        AutoRetrainModel(numberOfActions);
                        waitCount = 0;

                        // Reload new settings here for consistency
                        settings = LoadSettings();
                        serverObserveDelay = settings.ServerObserveDelay;
                        modelRetrainPeriodicDelay = settings.ModelRetrainPeriodicDelay;
                    }
                }
            }
        }

        static void AutoRetrainModel(int numberOfActions)
        {
            if (!LoadSettings().AutoRetrainModel)
            {
                return;
            }
            using (var client = new System.Net.Http.HttpClient())
            {
                var values = new Dictionary<string, string>
                {
                    { "token", appToken },
                    { "numberOfActions", numberOfActions.ToString() },
                    { "useAfx", LoadSettings().UseAfxForModelRetrain.ToString() }
                };

                var content = new System.Net.Http.FormUrlEncodedContent(values);

                var responseTask = client.PostAsync(
                    commandCenterAddress + "/Application/RetrainModel",
                    content
                );
                responseTask.Wait();

                var response = responseTask.Result;

                if (!response.IsSuccessStatusCode)
                {
                    var t2 = response.Content.ReadAsStringAsync();
                    t2.Wait();

                    Trace.TraceError(TraceMessage.GetHeader(TraceMessage.TraceComponentType.AzureML) + "Failed to request model retraining, Result: {0}, Reason: {1}, Headers: {2}.", 
                        t2.Result, response.ReasonPhrase, response.Headers.ToString());
                }
                else
                {
                    Trace.WriteLine(TraceMessage.GetHeader(TraceMessage.TraceComponentType.AzureML) + "Requested model retraining.");
                }
            }
        }

        public static void Reset()
        {
            // Clear trace messages
            DecisionServiceTrace.Clear();

            // Reset DecisionService objects
            if (Service != null)
            {
                Service.Flush();
                Service.Dispose();
            }

            Explorer = null;
            Configuration = null;
            Service = null;
            LastBlobModifiedDate = new DateTimeOffset();

            // Reset all settings via the command center (storage, metadata, etc...)
            using (var client = new System.Net.Http.HttpClient())
            {
                var values = new Dictionary<string, string>
                {
                    { "token", appToken }
                };
                var content = new System.Net.Http.FormUrlEncodedContent(values);

                var responseTask = client.PostAsync(
                    // TODO: use https
                    commandCenterAddress + "/Application/Reset",
                    content
                );
                responseTask.Wait();

                var response = responseTask.Result;
                if (!response.IsSuccessStatusCode)
                {
                    var t2 = response.Content.ReadAsStringAsync();
                    t2.Wait();
                    System.Diagnostics.Trace.TraceError("Failed to reset application. Result : {0}, Reason: {1}, Details: {2}", 
                        t2.Result, response.ReasonPhrase, response.Headers.ToString());
                }
            }
        }

        public static void ReportRewardForCachedProducts(ICacheManager cacheManager, int explorationJoinKeyIndex = -1)
        {
            if (cacheManager.IsSet(ProductController.JoinKeyCacheKey) && DecisionServiceWrapper<object>.Service != null)
            {
                var explorationKeys = cacheManager.Get<List<string>>(ProductController.JoinKeyCacheKey);
                for (int i = 0; i < explorationKeys.Count; i++)
                {
                    if (i != explorationJoinKeyIndex)
                    {
                        DecisionServiceWrapper<object>.Service.ReportReward(-1f, explorationKeys[i]);
                    }
                    else
                    {
                        DecisionServiceWrapper<object>.Service.ReportReward(1f, explorationKeys[i]);
                    }
                }

                var imageHtmlBuilder = new StringBuilder();

                if (cacheManager.IsSet(ProductController.CacheKey))
                {
                    var model = cacheManager.Get<IList<Nop.Web.Models.Catalog.ProductOverviewModel>>(ProductController.CacheKey);
                    for (int i = 0; i < model.Count; i++)
                    {
                        if (model[i].ExplorationJoinKeyIndex != -1)
                        {
                            string imageClass = string.Empty;
                            if (model[i].ExplorationJoinKeyIndex == explorationJoinKeyIndex)
                            {
                                imageClass = "mwt-rewarded";
                            }
                            imageHtmlBuilder.Append(string.Format("<img class=\"{0}\" src=\"{1}\" />", 
                                imageClass,
                                model[i].DefaultPictureModel.ImageUrl));
                        }
                    }
                }

                string imageHtml = imageHtmlBuilder.Length > 0 ? " <br /> <br /> " + imageHtmlBuilder.ToString() : imageHtmlBuilder.ToString();

                Trace.WriteLine(TraceMessage.GetHeader(TraceMessage.TraceComponentType.Client) + "Reported rewards for presented products" + imageHtml);

                // Clears cache once rewards have been determined.
                cacheManager.Remove(ProductController.JoinKeyCacheKey);

                CurrentTraceType.Value = TraceType.ClientToServerReward;
            }
        }

        private static DecisionServiceSettings LoadSettings()
        {
            try
            {
                return JsonConvert.DeserializeObject<DecisionServiceSettings>(File.ReadAllText(settingsFile));
            }
            catch
            {
                return new DecisionServiceSettings();
            }
        }
    }

    public static class DecisionServiceTrace
    {
        public static readonly int MaxTraceCount = 1000;

        static List<TraceMessage> traceMessageList = new List<TraceMessage>();

        public static List<TraceMessage> TraceMessageList
        {
            get { return DecisionServiceTrace.traceMessageList; }
        }

        public static void Add(TraceMessage trm) 
        {
            if (traceMessageList.Count >= DecisionServiceTrace.MaxTraceCount)
            {
                traceMessageList.Clear();
                DecisionServiceTrace.Add(new TraceMessage {
                    Message = string.Format("Max # trace messages received : {0}, resetting.", DecisionServiceTrace.MaxTraceCount)
                });
            }

            string lowerCaseMessage = trm.Message.ToLower();
            if (lowerCaseMessage.Contains("model update") ||
                lowerCaseMessage.Contains("successfully uploaded") ||
                lowerCaseMessage.Contains("retrieved new model"))
            {
                trm.Message = TraceMessage.GetHeader(TraceMessage.TraceComponentType.Client) + trm.Message;
            }

            traceMessageList.Add(trm);

            IHubContext hub = GlobalHost.ConnectionManager.GetHubContext<TraceHub>();
            hub.Clients.All.addNewMessageToPage(trm.Message, trm.TimeStampInMillisecSinceUnixEpoch);
        }

        public static void Clear()
        {
            traceMessageList.Clear();
        }
    }

    public class TraceMessage
    {
        public string Message { get; set; }
        public double TimeStampInMillisecSinceUnixEpoch { get; set; }

        public TraceMessage()
        {
            TimeStampInMillisecSinceUnixEpoch = DateTime.UtcNow
                .Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                .TotalMilliseconds;
        }

        public enum TraceComponentType
        {
            Client = 0,
            Server,
            AzureML
        }

        public static string GetHeader(TraceComponentType type)
        {
            string imageHtml = "<img src=\"{0}\" class=\"mwt-header\" /> {1}: ";
            switch (type)
            {
                case TraceComponentType.Client:
                    return string.Format(imageHtml, "/Themes/DefaultClean/Content/images/ico-client.png", type.ToString());
                case TraceComponentType.Server:
                    return string.Format(imageHtml, "/Themes/DefaultClean/Content/images/ico-server.png", type.ToString());
                case TraceComponentType.AzureML:
                    return string.Format(imageHtml, "/Themes/DefaultClean/Content/images/ico-azureml.png", type.ToString());
            }
            return string.Empty;
        }
    }

    class MartPolicy<TContext> : IPolicy<TContext>
    {
        private int action = 0;
        public MartPolicy(int action)
        {
            this.action = action;
        }
        public uint ChooseAction(TContext context)
        {
            return (uint)this.action;
        }
    }

    public class MartContext
    {
        public string Features { get; set; }
        public override string ToString()
        {
            return Features;
        }
    }

    public class ApplicationTransferMetadata
    {
        public string ApplicationID { get; set; }

        public string ConnectionString { get; set; }

        public string ModelId { get; set; }

        public int ExperimentalUnitDuration { get; set; }
    }
}