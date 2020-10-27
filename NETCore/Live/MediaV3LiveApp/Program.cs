using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Management.Media;
using Microsoft.Azure.Management.Media.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Rest;
using Microsoft.Rest.Azure.Authentication;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.IdentityModel.Clients.ActiveDirectory;

namespace LiveSample
{
    class Program
    {
        private static string liveEventName;

        public static async Task Main(string[] args)
        {
            ConfigWrapper config = new ConfigWrapper(new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build());

            try
            {
                await RunAsync(config);
            }
            catch (Exception exception)
            {
                if (exception.Source.Contains("ActiveDirectory"))
                {
                    Console.Error.WriteLine("TIP: Make sure that you have filled out the appsettings.json file before running this sample.");
                }

                Console.Error.WriteLine($"{exception.Message}");

                ApiErrorException apiException = exception.GetBaseException() as ApiErrorException;
                if (apiException != null)
                {
                    Console.Error.WriteLine(
                        $"ERROR: API call failed with error code '{apiException.Body.Error.Code}' and message '{apiException.Body.Error.Message}'.");
                }
            }

            Console.WriteLine("Press Enter to continue.");
            Console.ReadLine();

        }

        /// <summary>
        /// Run the sample async.
        /// </summary>
        /// <param name="config">The parm is of type ConfigWrapper. This class reads values from local configuration file.</param>
        /// <returns></returns>
        // <RunAsync>
        private static async Task RunAsync(ConfigWrapper config)
        {

            IAzureMediaServicesClient client = await CreateMediaServicesClientAsync(config);

            // Creating a unique suffix so that we don't have name collisions if you run the sample
            // multiple times without cleaning up.
            string uniqueness = Guid.NewGuid().ToString().Substring(0, 13);
            string liveEventName = "liveevent-" + uniqueness;
            string assetName = "archiveAsset" + uniqueness;
            string liveOutputName = "liveOutput" + uniqueness;
            string streamingLocatorName = "streamingLocator" + uniqueness;
            string streamingEndpointName = "default";

            try
            {
                // Getting the mediaServices account so that we can use the location to create the
                // LiveEvent and StreamingEndpoint
                MediaService mediaService = await client.Mediaservices.GetAsync(config.ResourceGroup, config.AccountName);

                #region CreateLiveEvent
                Console.WriteLine($"Creating a live event named {liveEventName}");
                Console.WriteLine();

                // Note: When creating a LiveEvent, you can specify allowed IP addresses in one of the following formats:                 
                //      IpV4 address with 4 numbers
                //      CIDR address range

                IPRange allAllowIPRange = new IPRange(
                    name: "AllowAll",
                    address: "0.0.0.0",
                    subnetPrefixLength: 0
                );

                // Create the LiveEvent input IP access control.
                LiveEventInputAccessControl liveEventInputAccess = new LiveEventInputAccessControl
                {
                    Ip = new IPAccessControl(
                            allow: new IPRange[]
                            {
                                allAllowIPRange
                            }
                        )

                };

                // Create the LiveEvent Preview IP access control
                LiveEventPreview liveEventPreview = new LiveEventPreview
                {
                    AccessControl = new LiveEventPreviewAccessControl(
                        ip: new IPAccessControl(
                            allow: new IPRange[]
                            {
                                allAllowIPRange
                            }
                        )
                    )
                };

                // To get the same ingest URL for the same LiveEvent name:
                // 1. Set vanityUrl to true so you have ingest like: 
                //        rtmps://liveevent-hevc12-eventgridmediaservice-usw22.channel.media.azure.net:2935/live/522f9b27dd2d4b26aeb9ef8ab96c5c77           
                // 2. Set accessToken to a desired GUID string (with or without hyphen)

                LiveEvent liveEvent = new LiveEvent(
                    location: mediaService.Location,
                    description: "Sample LiveEvent for testing",
                    vanityUrl: false,
                    encoding: new LiveEventEncoding(
                                // When encodingType is None, the service simply passes through the incoming video and audio layer(s) to the output
                                // When the encodingType is set to Standard or Premium1080p, a live encoder is used to transcode the incoming stream
                                // into multiple bit rates or layers. See https://go.microsoft.com/fwlink/?linkid=2095101 for more information
                                encodingType: LiveEventEncodingType.None,
                                presetName: null
                            ),
                    input: new LiveEventInput(LiveEventInputProtocol.RTMP, liveEventInputAccess),
                    preview: liveEventPreview,
                    streamOptions: new List<StreamOptionsFlag?>()
                    {
                        // Set this to Default or Low Latency
                        // When using Low Latency mode, you must configure the Azure Media Player to use the 
                        // quick start hueristic profile or you won't notice the change. 
                        // In the AMP player client side JS options, set -  heuristicProfile: "Low Latency Heuristic Profile". 
                        // To use low latency optimally, you should tune your encoder settings down to 1 second GOP size instead of 2 seconds.
                        StreamOptionsFlag.LowLatency
                    }
                );

                Console.WriteLine($"Creating the LiveEvent, be patient this can take time...");

                // When autostart is set to true, the Live Event will be started after creation. 
                // That means, the billing starts as soon as the Live Event starts running. 
                // You must explicitly call Stop on the Live Event resource to halt further billing.
                // The following operation can sometimes take awhile. Be patient.
                liveEvent = await client.LiveEvents.CreateAsync(config.ResourceGroup, config.AccountName, liveEventName, liveEvent, autoStart: true);
                #endregion

                // Get the input endpoint to configure the on premise encoder with
                #region GetIngestUrl
                string ingestUrl = liveEvent.Input.Endpoints.First().Url;
                Console.WriteLine($"The ingest url to configure the on premise encoder with is:");
                Console.WriteLine($"\t{ingestUrl}");
                Console.WriteLine();
                #endregion

                // Use the previewEndpoint to preview and verify
                // that the input from the encoder is actually being received
                #region GetPreviewURLs
                string previewEndpoint = liveEvent.Preview.Endpoints.First().Url;
                Console.WriteLine($"The preview url is:");
                Console.WriteLine($"\t{previewEndpoint}");
                Console.WriteLine();

                Console.WriteLine($"Open the live preview in your browser and use the Azure Media Player to monitor the preview playback:");
                Console.WriteLine($"\thttps://ampdemo.azureedge.net/?url={previewEndpoint}&heuristicprofile=lowlatency");
                Console.WriteLine();
                #endregion

                Console.WriteLine("Start the live stream now, sending the input to the ingest url and verify that it is arriving with the preview url.");
                Console.WriteLine("IMPORTANT TIP!: Make ABSOLUTLEY CERTAIN that the video is flowing to the Preview URL before continuing!");
                Console.WriteLine("Press enter to continue...");
                Console.Out.Flush();
                var ignoredInput = Console.ReadLine();

                // Create an Asset for the LiveOutput to use
                #region CreateAsset
                Console.WriteLine($"Creating an asset named {assetName}");
                Console.WriteLine();
                Asset asset = await client.Assets.CreateOrUpdateAsync(config.ResourceGroup, config.AccountName, assetName, new Asset());
                #endregion

                // Create the LiveOutput
                #region CreateLiveOutput
                string manifestName = "output";
                Console.WriteLine($"Creating a live output named {liveOutputName}");
                Console.WriteLine();

                LiveOutput liveOutput = new LiveOutput(assetName: asset.Name, manifestName: manifestName, archiveWindowLength: TimeSpan.FromMinutes(10));
                liveOutput = await client.LiveOutputs.CreateAsync(config.ResourceGroup, config.AccountName, liveEventName, liveOutputName, liveOutput);
                #endregion

                // Create the StreamingLocator
                #region CreateStreamingLocator
                Console.WriteLine($"Creating a streaming locator named {streamingLocatorName}");
                Console.WriteLine();

                StreamingLocator locator = new StreamingLocator(assetName: asset.Name, streamingPolicyName: PredefinedStreamingPolicy.ClearStreamingOnly);
                locator = await client.StreamingLocators.CreateAsync(config.ResourceGroup, config.AccountName, streamingLocatorName, locator);

                // Get the default Streaming Endpoint on the account
                StreamingEndpoint streamingEndpoint = await client.StreamingEndpoints.GetAsync(config.ResourceGroup, config.AccountName, streamingEndpointName);

                // If it's not running, Start it. 
                if (streamingEndpoint.ResourceState != StreamingEndpointResourceState.Running)
                {
                    Console.WriteLine("Streaming Endpoint was Stopped, restarting now..");
                    await client.StreamingEndpoints.StartAsync(config.ResourceGroup, config.AccountName, streamingEndpointName);
                }
                #endregion

                // Get the url to stream the output
                var paths = await client.StreamingLocators.ListPathsAsync(config.ResourceGroup, config.AccountName, streamingLocatorName);

                Console.WriteLine("The urls to stream the output from a client:");
                Console.WriteLine();
                StringBuilder stringBuilder = new StringBuilder();
                string playerPath = string.Empty;

                for (int i = 0; i < paths.StreamingPaths.Count; i++)
                {
                    UriBuilder uriBuilder = new UriBuilder();
                    uriBuilder.Scheme = "https";
                    uriBuilder.Host = streamingEndpoint.HostName;

                    if (paths.StreamingPaths[i].Paths.Count > 0)
                    {
                        uriBuilder.Path = paths.StreamingPaths[i].Paths[0];
                        stringBuilder.AppendLine($"\t{paths.StreamingPaths[i].StreamingProtocol}-{paths.StreamingPaths[i].EncryptionScheme}");
                        stringBuilder.AppendLine($"\t\t{uriBuilder.ToString()}");
                        stringBuilder.AppendLine();

                        if (paths.StreamingPaths[i].StreamingProtocol == StreamingPolicyStreamingProtocol.Dash)
                        {
                            playerPath = uriBuilder.ToString();
                        }
                    }
                }

                if (stringBuilder.Length > 0)
                {
                    Console.WriteLine(stringBuilder.ToString());
                    Console.WriteLine("Open the following URL to playback the published,recording LiveOutput in the Azure Media Player");
                    Console.WriteLine($"\t https://ampdemo.azureedge.net/?url={playerPath}&heuristicprofile=lowlatency");
                    Console.WriteLine();

                    Console.WriteLine("Continue experimenting with the stream until you are ready to finish.");
                    Console.WriteLine("Press enter to stop the LiveOutput...");
                    Console.Out.Flush();
                    ignoredInput = Console.ReadLine();

                    await CleanupLiveEventAndOutputAsync(client, config.ResourceGroup, config.AccountName, liveEventName);

                    Console.WriteLine("The LiveOutput and LiveEvent are now deleted.  The event is available as an archive and can still be streamed.");
                    Console.WriteLine("Press enter to finish cleanup...");
                    Console.Out.Flush();
                    ignoredInput = Console.ReadLine();
                }
                else
                {
                    Console.WriteLine("No Streaming Paths were detected.  Has the Stream been started?");
                    Console.WriteLine("Cleaning up and Exiting...");
                }
            }
            catch (ApiErrorException e)
            {
                Console.WriteLine("Hit ApiErrorException");
                Console.WriteLine($"\tCode: {e.Body.Error.Code}");
                Console.WriteLine($"\tCode: {e.Body.Error.Message}");
                Console.WriteLine();
                Console.WriteLine("Exiting, cleanup may be necessary...");
                Console.ReadLine();
            }
            finally
            {
                await CleanupLiveEventAndOutputAsync(client, config.ResourceGroup, config.AccountName, liveEventName);

                await CleanupLocatorandAssetAsync(client, config.ResourceGroup, config.AccountName, streamingLocatorName, assetName);
            }
        }

        private static async Task<LiveEvent> CreateLiveEventAsync(IAzureMediaServicesClient client, string resourceGroup, string accountName, string liveEventName)
        {
            // Getting the mediaServices account so that we can use the location to create the
            // LiveEvent and StreamingEndpoint
            MediaService mediaService = await client.Mediaservices.GetAsync(resourceGroup, accountName);

            Console.WriteLine($"Creating a live event named {liveEventName}");
            Console.WriteLine();

            // Note: When creating a LiveEvent, you can specify allowed IP addresses in one of the following formats:                 
            //      IpV4 address with 4 numbers
            //      CIDR address range

            IPRange allAllowIPRange = new IPRange(
                name: "AllowAll",
                address: "0.0.0.0",
                subnetPrefixLength: 0
            );

            // Create the LiveEvent input IP access control.
            LiveEventInputAccessControl liveEventInputAccess = new LiveEventInputAccessControl
            {
                Ip = new IPAccessControl(
                        allow: new IPRange[]
                        {
                                allAllowIPRange
                        }
                    )

            };

            // Create the LiveEvent Preview IP access control
            LiveEventPreview liveEventPreview = new LiveEventPreview
            {
                AccessControl = new LiveEventPreviewAccessControl(
                    ip: new IPAccessControl(
                        allow: new IPRange[]
                        {
                                allAllowIPRange
                        }
                    )
                )
            };

            // To get the same ingest URL for the same LiveEvent name:
            // 1. Set vanityUrl to true so you have ingest like: 
            //        rtmps://liveevent-hevc12-eventgridmediaservice-usw22.channel.media.azure.net:2935/live/522f9b27dd2d4b26aeb9ef8ab96c5c77           
            // 2. Set accessToken to a desired GUID string (with or without hyphen)

            LiveEvent liveEvent = new LiveEvent(
                location: mediaService.Location,
                description: "Sample LiveEvent for testing",
                vanityUrl: false,
                encoding: new LiveEventEncoding(
                            // Set this to Standard to enable a transcoding LiveEvent, and None to enable a pass-through LiveEvent
                            encodingType: LiveEventEncodingType.None,
                            presetName: null
                        ),
                input: new LiveEventInput(LiveEventInputProtocol.RTMP, liveEventInputAccess),
                preview: liveEventPreview,
                streamOptions: new List<StreamOptionsFlag?>()
                {
                        // Set this to Default or Low Latency
                        // When using Low Latency mode, you must configure the Azure Media Player to use the 
                        // quick start hueristic profile or you won't notice the change. 
                        // In the AMP player client side JS options, set -  heuristicProfile: "Low Latency Heuristic Profile". 
                        // To use low latency optimally, you should tune your encoder settings down to 1 second GOP size instead of 2 seconds.
                        StreamOptionsFlag.LowLatency
                }
            );

            Console.WriteLine($"Creating the LiveEvent, be patient this can take time...");

            // When autostart is set to true, the Live Event will be started after creation. 
            // That means, the billing starts as soon as the Live Event starts running. 
            // You must explicitly call Stop on the Live Event resource to halt further billing.
            // The following operation can sometimes take awhile. Be patient.
            return await client.LiveEvents.CreateAsync(resourceGroup, accountName, liveEventName, liveEvent, autoStart: true);
        }

        private static async Task<StreamingLocator> CreateAssetAndLocatorAsync(IAzureMediaServicesClient client, string resourceGroup, string accountName, string assetName, string streamingLocatorName)
        {
            // Create an Asset for the LiveOutput to use
            Console.WriteLine($"Creating an asset named {assetName}");
            Console.WriteLine();
            Asset asset = await client.Assets.CreateOrUpdateAsync(resourceGroup, accountName, assetName, new Asset());

            // Create the StreamingLocator
            Console.WriteLine($"Creating a streaming locator named {streamingLocatorName}");
            Console.WriteLine();

            StreamingLocator locator = new StreamingLocator(assetName: asset.Name, streamingPolicyName: PredefinedStreamingPolicy.ClearStreamingOnly);
            return await client.StreamingLocators.CreateAsync(resourceGroup, accountName, streamingLocatorName, locator);
        }

        private static async Task<StreamingEndpoint> StartStreamingEndpointAsync(IAzureMediaServicesClient client, string resourceGroup, string accountName, string assetName, string streamingEndpointName)
        {
            // Get the default Streaming Endpoint on the account
            StreamingEndpoint streamingEndpoint = await client.StreamingEndpoints.GetAsync(resourceGroup, accountName, streamingEndpointName);

            // If it's not running, Start it. 
            if (streamingEndpoint.ResourceState != StreamingEndpointResourceState.Running)
            {
                Console.WriteLine("Streaming Endpoint was Stopped, restarting now..");
                await client.StreamingEndpoints.StartAsync(resourceGroup, accountName, streamingEndpointName);

                streamingEndpoint = await client.StreamingEndpoints.GetAsync(resourceGroup, accountName, streamingEndpointName);
            }

            return streamingEndpoint;
        }

        private static async Task RunAsync2(ConfigWrapper config)
        {
            IAzureMediaServicesClient client = await CreateMediaServicesClientAsync(config);

            // Creating a unique suffix so that we don't have name collisions if you run the sample
            // multiple times without cleaning up.
            string uniqueness = Guid.NewGuid().ToString().Substring(0, 13);
            string liveEventName = "liveevent-" + uniqueness;
            string assetName = "archiveAsset" + uniqueness;
            string liveOutputName = "liveOutput" + uniqueness;
            string streamingLocatorName = "streamingLocator" + uniqueness;
            string streamingEndpointName = "default";

            try
            {
                Task<LiveEvent> liveEventTask = CreateLiveEventAsync(client, config.ResourceGroup, config.AccountName, liveEventName);
                Task<StreamingLocator> streamingLocatorTask = CreateAssetAndLocatorAsync(client, config.ResourceGroup, config.AccountName, assetName, streamingLocatorName);
                Task<StreamingEndpoint> startStreamingEndpointTask = StartStreamingEndpointAsync(client, config.ResourceGroup, config.AccountName, assetName, streamingEndpointName);

                Task[] tasks = new Task[]
                {
                    liveEventTask,
                    streamingLocatorTask,
                    startStreamingEndpointTask
                };

                Task.WaitAll(tasks);

                LiveEvent liveEvent = liveEventTask.Result;
                StreamingEndpoint streamingEndpoint = startStreamingEndpointTask.Result;

                // Get the input endpoint to configure the on premise encoder with
                string ingestUrl = liveEvent.Input.Endpoints.First().Url;
                Console.WriteLine($"The ingest url to configure the on premise encoder with is:");
                Console.WriteLine($"\t{ingestUrl}");
                Console.WriteLine();

                // Use the previewEndpoint to preview and verify
                // that the input from the encoder is actually being received
                string previewEndpoint = liveEvent.Preview.Endpoints.First().Url;
                Console.WriteLine($"The preview url is:");
                Console.WriteLine($"\t{previewEndpoint}");
                Console.WriteLine();

                Console.WriteLine($"Open the live preview in your browser and use the Azure Media Player to monitor the preview playback:");
                Console.WriteLine($"\thttps://ampdemo.azureedge.net/?url={previewEndpoint}&heuristicprofile=lowlatency");
                Console.WriteLine();

                Console.WriteLine("Start the live stream now, sending the input to the ingest url and verify that it is arriving with the preview url.");
                Console.WriteLine("IMPORTANT TIP!: Make ABSOLUTLEY CERTAIN that the video is flowing to the Preview URL before continuing!");
                Console.WriteLine("Press enter to continue...");
                Console.Out.Flush();
                var ignoredInput = Console.ReadLine();

                // Create the LiveOutput
                string manifestName = "output";
                Console.WriteLine($"Creating a live output named {liveOutputName}");
                Console.WriteLine();

                LiveOutput liveOutput = new LiveOutput(assetName: assetName, manifestName: manifestName, archiveWindowLength: TimeSpan.FromMinutes(10));
                liveOutput = await client.LiveOutputs.CreateAsync(config.ResourceGroup, config.AccountName, liveEventName, liveOutputName, liveOutput);

                // Get the url to stream the output
                var paths = await client.StreamingLocators.ListPathsAsync(config.ResourceGroup, config.AccountName, streamingLocatorName);

                Console.WriteLine("The urls to stream the output from a client:");
                Console.WriteLine();
                StringBuilder stringBuilder = new StringBuilder();
                string playerPath = string.Empty;

                for (int i = 0; i < paths.StreamingPaths.Count; i++)
                {
                    UriBuilder uriBuilder = new UriBuilder();
                    uriBuilder.Scheme = "https";
                    uriBuilder.Host = streamingEndpoint.HostName;

                    if (paths.StreamingPaths[i].Paths.Count > 0)
                    {
                        uriBuilder.Path = paths.StreamingPaths[i].Paths[0];
                        stringBuilder.AppendLine($"\t{paths.StreamingPaths[i].StreamingProtocol}-{paths.StreamingPaths[i].EncryptionScheme}");
                        stringBuilder.AppendLine($"\t\t{uriBuilder.ToString()}");
                        stringBuilder.AppendLine();

                        if (paths.StreamingPaths[i].StreamingProtocol == StreamingPolicyStreamingProtocol.Dash)
                        {
                            playerPath = uriBuilder.ToString();
                        }
                    }
                }

                if (stringBuilder.Length > 0)
                {
                    Console.WriteLine(stringBuilder.ToString());
                    Console.WriteLine("Open the following URL to playback the published,recording LiveOutput in the Azure Media Player");
                    Console.WriteLine($"\t https://ampdemo.azureedge.net/?url={playerPath}&heuristicprofile=lowlatency");
                    Console.WriteLine();

                    Console.WriteLine("Continue experimenting with the stream until you are ready to finish.");
                    Console.WriteLine("Press enter to stop the LiveOutput...");
                    Console.Out.Flush();
                    ignoredInput = Console.ReadLine();

                    await CleanupLiveEventAndOutputAsync(client, config.ResourceGroup, config.AccountName, liveEventName);

                    Console.WriteLine("The LiveOutput and LiveEvent are now deleted.  The event is available as an archive and can still be streamed.");
                    Console.WriteLine("Press enter to finish cleanup...");
                    Console.Out.Flush();
                    ignoredInput = Console.ReadLine();
                }
                else
                {
                    Console.WriteLine("No Streaming Paths were detected.  Has the Stream been started?");
                    Console.WriteLine("Cleaning up and Exiting...");
                }
            }
            catch (ApiErrorException e)
            {
                Console.WriteLine("Hit ApiErrorException");
                Console.WriteLine($"\tCode: {e.Body.Error.Code}");
                Console.WriteLine($"\tCode: {e.Body.Error.Message}");
                Console.WriteLine();
                Console.WriteLine("Exiting, cleanup may be necessary...");
                Console.ReadLine();
            }
            finally
            {
                await CleanupLiveEventAndOutputAsync(client, config.ResourceGroup, config.AccountName, liveEventName);

                await CleanupLocatorandAssetAsync(client, config.ResourceGroup, config.AccountName, streamingLocatorName, assetName);
            }
        }


        /// <summary>
        /// Create the ServiceClientCredentials object based on the credentials
        /// supplied in local configuration file.
        /// </summary>
        /// <param name="config">The parm is of type ConfigWrapper. This class reads values from local configuration file.</param>
        /// <returns></returns>
        // <GetCredentialsAsync>
        private static async Task<ServiceClientCredentials> GetCredentialsAsync(ConfigWrapper config)
        {
            // Use ApplicationTokenProvider.LoginSilentWithCertificateAsync or UserTokenProvider.LoginSilentAsync to get a token using service principal with certificate
            //// ClientAssertionCertificate
            //// ApplicationTokenProvider.LoginSilentWithCertificateAsync

            // Use ApplicationTokenProvider.LoginSilentAsync to get a token using a service principal with symetric key
            ClientCredential clientCredential = new ClientCredential(config.AadClientId, config.AadSecret);
            return await ApplicationTokenProvider.LoginSilentAsync(config.AadTenantId, clientCredential, ActiveDirectoryServiceSettings.Azure);
        }
        // </GetCredentialsAsync>

        /// <summary>
        /// Creates the AzureMediaServicesClient object based on the credentials
        /// supplied in local configuration file.
        /// </summary>
        /// <param name="config">The parm is of type ConfigWrapper. This class reads values from local configuration file.</param>
        /// <returns></returns>
        // <CreateMediaServicesClient>
        private static async Task<IAzureMediaServicesClient> CreateMediaServicesClientAsync(ConfigWrapper config)
        {
            var credentials = await GetCredentialsAsync(config);

            return new AzureMediaServicesClient(config.ArmEndpoint, credentials)
            {
                SubscriptionId = config.SubscriptionId,
            };
        }
        // </CreateMediaServicesClient>

        // <CleanupLiveEventAndOutput>
        private static async Task CleanupLiveEventAndOutputAsync(IAzureMediaServicesClient client, string resourceGroup, string accountName, string liveEventName)
        {
            try
            {
                LiveEvent liveEvent = await client.LiveEvents.GetAsync(resourceGroup, accountName, liveEventName);

                if (liveEvent != null)
                {
                    if (liveEvent.ResourceState == LiveEventResourceState.Running)
                    {
                        // If the LiveEvent is running, stop it and have it remove any LiveOutputs
                        await client.LiveEvents.StopAsync(resourceGroup, accountName, liveEventName, removeOutputsOnStop: true);
                    }

                    // Delete the LiveEvent
                    await client.LiveEvents.DeleteAsync(resourceGroup, accountName, liveEventName);
                }
            }
            catch (ApiErrorException e)
            {
                Console.WriteLine("CleanupLiveEventAndOutputAsync -- Hit ApiErrorException");
                Console.WriteLine($"\tCode: {e.Body.Error.Code}");
                Console.WriteLine($"\tCode: {e.Body.Error.Message}");
                Console.WriteLine();
            }
        }
        // </CleanupLiveEventAndOutput>

        // <CleanupLocatorAssetAndStreamingEndpoint>
        private static async Task CleanupLocatorandAssetAsync(IAzureMediaServicesClient client, string resourceGroup, string accountName, string streamingLocatorName, string assetName)
        {
            try
            {
                // Delete the Streaming Locator
                await client.StreamingLocators.DeleteAsync(resourceGroup, accountName, streamingLocatorName);

                // Delete the Archive Asset
                await client.Assets.DeleteAsync(resourceGroup, accountName, assetName);
            }
            catch (ApiErrorException e)
            {
                Console.WriteLine("CleanupLocatorandAssetAsync -- Hit ApiErrorException");
                Console.WriteLine($"\tCode: {e.Body.Error.Code}");
                Console.WriteLine($"\tCode: {e.Body.Error.Message}");
                Console.WriteLine();
            }
        }
        // </CleanupLocatorAssetAndStreamingEndpoint>

        private static async Task CleanupStreamingEndpointAsync(IAzureMediaServicesClient client, string resourceGroup, string accountName, string streamingEndpointName, bool stopEndpoint, bool deleteEndpoint)
        {
            try
            {
                if (stopEndpoint || deleteEndpoint)
                {
                    StreamingEndpoint s = await client.StreamingEndpoints.GetAsync(resourceGroup, accountName, streamingEndpointName);

                    if (s != null && s.ResourceState == StreamingEndpointResourceState.Running)
                    {
                        // Stop the StreamingEndpoint
                        await client.StreamingEndpoints.StopAsync(resourceGroup, accountName, streamingEndpointName);
                    }

                    if (deleteEndpoint)
                    {
                        // Delete the StreamingEndpoint
                        await client.StreamingEndpoints.DeleteAsync(resourceGroup, accountName, streamingEndpointName);
                    }
                }
            }
            catch (ApiErrorException e)
            {
                Console.WriteLine("CleanupStreamingEndpointAsync -- Hit ApiErrorException");
                Console.WriteLine($"\tCode: {e.Body.Error.Code}");
                Console.WriteLine($"\tCode: {e.Body.Error.Message}");
                Console.WriteLine();
            }
        }
    }
}

