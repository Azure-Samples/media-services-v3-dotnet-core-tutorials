using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Management.Media;
using Microsoft.Azure.Management.Media.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Rest;
using Microsoft.Rest.Azure.Authentication;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.IdentityModel.Clients.ActiveDirectory;

namespace AnalyzeVideos
{
    public class Program
    {
        const String inputMP4FileName = @"ignite.mp4";
        const String outputFolder = @"Output";
        const String VideoAnalyzerTransformName = "VideoAnalyzerTransform";

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
            // Set the polling interval for long running operations to 2 seconds.
            // The default value is 30 seconds for the .NET client SDK
            client.LongRunningOperationRetryTimeout = 2;

            try
            {
                // Ensure that you have customized transforms for the VideoAnalyzer.  This is really a one time setup operation.
                Transform videoAnalyzerTransform = EnsureTransformExists(client,config.ResourceGroup, config.AccountName, VideoAnalyzerTransformName, new VideoAnalyzerPreset());

                // Creating a unique suffix so that we don't have name collisions if you run the sample
                // multiple times without cleaning up.
                string uniqueness = Guid.NewGuid().ToString().Substring(0, 13);
                string jobName = "job-" + uniqueness;
                string inputAssetName = "input-" + uniqueness;
                string outputAssetName = "output-" + uniqueness;

                CreateInputAsset(client, config.ResourceGroup, config.AccountName, inputAssetName, inputMP4FileName).Wait();

                JobInput input = new JobInputAsset(assetName: inputAssetName);

                Asset outputAsset = CreateOutputAsset(client,config.ResourceGroup, config.AccountName, outputAssetName);

                // Note that you can now pass custom correlation data Dictionary into the job to use via EventGrid or other Job polling listeners.
                // this is handy for passing tenant ID, or custom workflow data as part of your job.
                var correlationData = new Dictionary<string,string>();
                correlationData.Add("customData1", "some custom data to pass through the job");
                correlationData.Add("custom ID", "some GUID here");

                Job job = SubmitJob(client,config.ResourceGroup, config.AccountName, VideoAnalyzerTransformName, jobName, input, outputAsset.Name, correlationData);

                DateTime startedTime = DateTime.Now;

                job = WaitForJobToFinish(client,config.ResourceGroup, config.AccountName,  VideoAnalyzerTransformName, jobName);

                TimeSpan elapsed = DateTime.Now - startedTime;

                if (job.State == JobState.Finished)
                {
                    Console.WriteLine("Job finished.");
                    if (!Directory.Exists(outputFolder))
                        Directory.CreateDirectory(outputFolder);
                    DownloadResults(client, config.ResourceGroup, config.AccountName, outputAsset.Name, outputFolder).Wait();
                }
                else if (job.State == JobState.Error)
                {
                    Console.WriteLine($"ERROR: Job finished with error message: {job.Outputs[0].Error.Message}");
                    Console.WriteLine($"ERROR:                   error details: {job.Outputs[0].Error.Details[0].Message}");
                }
            }
            catch(ApiErrorException ex)
            {
                string code = ex.Body.Error.Code;
                string message = ex.Body.Error.Message;

                Console.WriteLine("ERROR:API call failed with error code: {0} and message: {1}", code, message);

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

       private static Transform EnsureTransformExists(IAzureMediaServicesClient client, string resourceGroupName, string accountName, string transformName, Preset preset)
        {
            Transform transform = client.Transforms.Get(resourceGroupName, accountName, transformName);

            if (transform == null)
            {
                TransformOutput[] outputs = new TransformOutput[]
                {
                    new TransformOutput(preset),
                };

                transform = client.Transforms.CreateOrUpdate(resourceGroupName, accountName, transformName, outputs);
            }

            return transform;
        }

        private static async Task<Asset> CreateInputAsset(IAzureMediaServicesClient client, string resourceGroupName, string accountName, string assetName, string fileToUpload)
        {
            Console.WriteLine("Creating Input Asset");
            Asset asset = client.Assets.CreateOrUpdate(resourceGroupName, accountName,assetName, new Asset());



            ListContainerSasInput input = new ListContainerSasInput()
            {
                Permissions = AssetContainerPermission.ReadWrite,
                ExpiryTime = DateTime.Now.AddHours(2).ToUniversalTime()
            };

            var response = client.Assets.ListContainerSasAsync(resourceGroupName, accountName, assetName, input.Permissions,input.ExpiryTime).Result;

            string uploadSasUrl = response.AssetContainerSasUrls.First();

            string filename = Path.GetFileName(fileToUpload);
            Console.WriteLine("Uploading file: {0}", filename);

            var sasUri = new Uri(uploadSasUrl);
            CloudBlobContainer container = new CloudBlobContainer(sasUri);
            var blob = container.GetBlockBlobReference(filename);
            blob.Properties.ContentType = "video/mp4";
            Console.WriteLine("Uploading File to container: {0}", sasUri);
            await blob.UploadFromFileAsync(fileToUpload);

            return asset;
        }

        private static Asset CreateOutputAsset(IAzureMediaServicesClient client, string resourceGroupName, string accountName, string assetName)
        {
            Asset input = new Asset();

            return client.Assets.CreateOrUpdate(resourceGroupName, accountName, assetName, input);
        }

        private static Job SubmitJob(IAzureMediaServicesClient client, string resourceGroupName, string accountName, string transformName, string jobName, JobInput jobInput, string outputAssetName, Dictionary<string,string> correlationData)
        {
            JobOutput[] jobOutputs =
            {
                new JobOutputAsset(outputAssetName), 
            };

            Job job = client.Jobs.Create(
                resourceGroupName, 
                accountName,
                transformName,
                jobName,
                new Job
                {
                    Input = jobInput,
                    Outputs = jobOutputs,
                    CorrelationData = correlationData
                });

            return job;
        }


        private static Job WaitForJobToFinish(IAzureMediaServicesClient client, string resourceGroupName, string accountName, string transformName, string jobName)
        {
            const int SleepInterval = 10 * 1000;

            Job job = null;
            bool exit = false;

            do
            {
                job = client.Jobs.Get(resourceGroupName, accountName, transformName, jobName);
                
                if (job.State == JobState.Finished || job.State == JobState.Error || job.State == JobState.Canceled)
                {
                    exit = true;
                }
                else
                {
                    Console.WriteLine($"Job is {job.State}.");

                    for (int i = 0; i < job.Outputs.Count; i++)
                    {
                        JobOutput output = job.Outputs[i];

                        Console.Write($"\tJobOutput[{i}] is {output.State}.");

                        if (output.State == JobState.Processing)
                        {
                            Console.Write($"  Progress: {output.Progress}");
                        }

                        Console.WriteLine();
                    }

                    System.Threading.Thread.Sleep(SleepInterval);
                }
            }
            while (!exit);

            return job;
        }



        private async static Task DownloadResults(IAzureMediaServicesClient client, string resourceGroupName, string accountName, string assetName, string resultsFolder)
        {
            ListContainerSasInput parameters = new ListContainerSasInput();
            AssetContainerSas assetContainerSas = client.Assets.ListContainerSas(
                            resourceGroupName, 
                            accountName, 
                            assetName,
                            permissions: AssetContainerPermission.Read, 
                            expiryTime: DateTime.UtcNow.AddHours(1).ToUniversalTime()
                            );

            Uri containerSasUrl = new Uri(assetContainerSas.AssetContainerSasUrls.FirstOrDefault());
            CloudBlobContainer container = new CloudBlobContainer(containerSasUrl);

            string directory = Path.Combine(resultsFolder, assetName);
            Directory.CreateDirectory(directory);

            Console.WriteLine("Downloading results to {0}.", directory);
            
            var blobs = container.ListBlobsSegmentedAsync(null,true, BlobListingDetails.None,200,null,null,null).Result;
            
            foreach (var blobItem in blobs.Results)
            {
                if (blobItem is CloudBlockBlob)
                {
                    CloudBlockBlob blob = blobItem as CloudBlockBlob;
                    string filename = Path.Combine(directory, blob.Name);

                    await blob.DownloadToFileAsync(filename, FileMode.Create);
                }
            }

            Console.WriteLine("Download complete.");
            
        }

    }
}
