using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Azure.Management.Media;
using Microsoft.Azure.Management.Media.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Rest;
using Microsoft.Rest.Azure.Authentication;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.IdentityModel.Clients.ActiveDirectory;

namespace EncodeAndStream
{
    class Program
    {
        const String outputFolder = @"Output";
        const String transformName = "AdaptiveBitrate";

        private static string Issuer = "myIssuer";
        private static string Audience = "myAudience";
        private static byte[] TokenSigningKey = new byte[40];
        private static string ContentKeyPolicyName = "SharedContentKeyPolicyUsedByAllAssets2";

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
                // Generate a new random token signing key to use
                RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
                rng.GetBytes(TokenSigningKey);

                //Create the content key policy that configures how the content key is delivered to end clients
                // via the Key Delivery component of Azure Media Services.
                ContentKeyPolicy policy = EnsureContentKeyPolicyExists(client, config.ResourceGroup, config.AccountName, ContentKeyPolicyName);

                // Ensure that you have customized encoding Transform.  This is really a one time setup operation.
                Transform adaptiveEncodeTransform = EnsureTransformExists(client, config.ResourceGroup, config.AccountName, transformName, preset: new BuiltInStandardEncoderPreset(EncoderNamedPreset.AdaptiveStreaming));

                // Creating a unique suffix so that we don't have name collisions if you run the sample
                // multiple times without cleaning up.
                string uniqueness = Guid.NewGuid().ToString().Substring(0, 13);

                string jobName = "job-" + uniqueness;
                string inputAssetName = "input-" + uniqueness;
                string outputAssetName = "output-" + uniqueness;
                string streamingLocatorName =  "locator-" + uniqueness;

                Asset asset = client.Assets.CreateOrUpdate(config.ResourceGroup, config.AccountName,  inputAssetName, new Asset());

                var input = new JobInputHttp(
                                    baseUri: "https://nimbuscdn-nimbuspm.streaming.mediaservices.windows.net/2b533311-b215-4409-80af-529c3e853622/",
                                    files: new List<String> {"Ignite-short.mp4"},
                                    label:"input1"
                                    );
                

                CreateOutputAsset(client, config.ResourceGroup, config.AccountName, outputAssetName);

                Job job = SubmitJob(client, config.ResourceGroup, config.AccountName, transformName, jobName, input, outputAssetName);

                DateTime startedTime = DateTime.Now;

                job = WaitForJobToFinish(client, config.ResourceGroup, config.AccountName, transformName, jobName);

                TimeSpan elapsed = DateTime.Now - startedTime;

                if (job.State == JobState.Finished)
                {
                    Console.WriteLine("Job finished.");
                    
                    // Now that the content has been encoded, publish it for Streaming by creating
                    // a StreamingLocator.  Note that we are using one of the PredefinedStreamingPolicies
                    // which tell the Origin component of Azure Media Services how to publish the content
                    // for streaming.  In this case it applies AES Envelople encryption, which is also known
                    // ClearKey encryption (because the key is delivered to the playback client via HTTPS and
                    // not instead a DRM license).
                    StreamingLocator locator = new StreamingLocator(
                        assetName: outputAssetName,
                        streamingPolicyName: PredefinedStreamingPolicy.ClearKey,
                        defaultContentKeyPolicyName: ContentKeyPolicyName);

                    client.StreamingLocators.Create(config.ResourceGroup, config.AccountName, streamingLocatorName, locator);
                    
                    // We are using the ContentKeyIdentifierClaim in the ContentKeyPolicy which means that the token presented
                    // to the Key Delivery Component must have the identifier of the content key in it.  Since we didn't specify
                    // a content key when creating the StreamingLocator, the system created a random one for us.  In order to 
                    // generate our test token we must get the ContentKeyId to put in the ContentKeyIdentifierClaim claim.
                    var response = client.StreamingLocators.ListContentKeys(config.ResourceGroup, config.AccountName, streamingLocatorName);
                    string keyIdentifier = response.ContentKeys.First().Id.ToString();

                    // We can either use the "default" StreamingEndpoint or we can create a new StreamingEndpoint.
                    // Typically we would just ensure that the default endpoint was started but let's create one
                    // here to illustrate how it is done.

                    // Console.WriteLine($"Creating a streaming endpoint named {endpointName}");
                    // Console.WriteLine();

                    //StreamingEndpoint streamingEndpoint = new StreamingEndpoint(location: mediaService.Location);
                    var streamingEndpoint = client.StreamingEndpoints.Get(config.ResourceGroup, config.AccountName, "default");
                    
                    // Get the URls to stream the output
                    var paths = client.StreamingLocators.ListPaths(config.ResourceGroup, config.AccountName, streamingLocatorName);

                    Console.WriteLine("The urls to stream the output from a client:");
                    Console.WriteLine();

                    var token = GetToken(Issuer, Audience, keyIdentifier, TokenSigningKey);

                    for (int i = 0; i < paths.StreamingPaths.Count; i++)
                    {
                        UriBuilder uriBuilder = new UriBuilder();
                        uriBuilder.Scheme = "https";
                        uriBuilder.Host = streamingEndpoint.HostName;

                        if (paths.StreamingPaths[i].Paths.Count > 0)
                        {
                            //uriBuilder.Path = paths.StreamingPaths[i].Paths[0];
                            //Console.WriteLine($"\t{paths.StreamingPaths[i].StreamingProtocol}-{paths.StreamingPaths[i].EncryptionScheme}");
                            //Console.WriteLine($"\t\t{uriBuilder.ToString()}");
                            //Console.WriteLine();

                            // Look for just the DASH path and generate a URL for the Azure Media Player to playback the content with the AES token to decrypt.
                            // Note that the JWT token is set to expire in 1 hour. 
                            if (paths.StreamingPaths[i].StreamingProtocol== "Dash"){
                                uriBuilder.Path = paths.StreamingPaths[i].Paths[0];
                                var dashPath = uriBuilder.ToString();

                                Console.WriteLine("Open the following URL in your browser to play back the file in the Azure Media Player");
                                Console.WriteLine($"https://ampdemo.azureedge.net/?url={dashPath}&aes=true&aestoken=Bearer%3D{token}");
                                Console.WriteLine();
                            }
                        }
                    }

                   

                  

                }
                else if (job.State == JobState.Error)
                {
                    Console.WriteLine($"ERROR: Job finished with error message: {job.Outputs[0].Error.Message}");
                    Console.WriteLine($"ERROR:                   error details: {job.Outputs[0].Error.Details[0].Message}");
                }

                Console.WriteLine("Try Streaming the content using Azure Media Player - https://ampdemo.azureedge.net.");
                Console.WriteLine("Use the Advanced options to set the AES token in the AMP demo page. When finished press enter to cleanup.");
                Console.Out.Flush();
                Console.ReadLine();

                Console.WriteLine("Cleaning up...");
                Cleanup(client, config.ResourceGroup, config.AccountName, transformName, jobName, outputAssetName, input, streamingLocatorName, ContentKeyPolicyName);
       
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
            // Use UserTokenProvider.LoginWithPromptAsync or UserTokenProvider.LoginSilentAsync to get a token using user authentication
            //// ActiveDirectoryClientSettings.UsePromptOnly
            //// UserTokenProvider.LoginWithPromptAsync

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

        private static void Cleanup(IAzureMediaServicesClient client, string resourceGroupName, string accountName, string transformName, string jobName, string outputAssetName, JobInput input, string streamingLocatorName, string contentKeyPolicyName)
        {
            client.Jobs.Delete(resourceGroupName, accountName, transformName, jobName);
            client.Assets.Delete(resourceGroupName, accountName, outputAssetName);

            JobInputAsset jobInputAsset = input as JobInputAsset;
            if (jobInputAsset != null)
            {
                client.Assets.Delete(resourceGroupName, accountName, jobInputAsset.AssetName);
            }

            // Delete the Streaming Locator
            client.StreamingLocators.Delete(resourceGroupName, accountName, streamingLocatorName);

            // Stop and delete the StreamingEndpoint - use this if you are creating a custom (non default) endpoint
            //client.StreamingEndpoints.Stop(resourceGroupName, accountName, endpointName);
            //client.StreamingEndpoints.Delete(resourceGroupName, accountName, endpointName);

            client.ContentKeyPolicies.Delete(resourceGroupName, accountName, contentKeyPolicyName);
        }

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
     

        private static Asset CreateOutputAsset(IAzureMediaServicesClient client, string resourceGroupName, string accountName,  string assetName)
        {
            Asset input = new Asset();

            return client.Assets.CreateOrUpdate(resourceGroupName, accountName, assetName, input);
        }

        private static Job SubmitJob(IAzureMediaServicesClient client, string resourceGroupName, string accountName, string transformName, string jobName, JobInput jobInput, string outputAssetName)
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

         private static void PublishAssetWithEnvelopeEncryption(IAzureMediaServicesClient client, string resourceGroup, string accountName, string assetName, string streamingLocatorName)
        {
            string contentKeyPolicyName = "SharedContentKeyPolicyUsedByAllAssets";
            ContentKeyPolicy policy = EnsureContentKeyPolicyExists(client, resourceGroup, accountName, contentKeyPolicyName);

            StreamingLocator locator = new StreamingLocator(
                assetName: assetName,
                streamingPolicyName: PredefinedStreamingPolicy.ClearKey,
                defaultContentKeyPolicyName: contentKeyPolicyName);

            client.StreamingLocators.Create(resourceGroup, accountName, streamingLocatorName, locator);
        }

        private static string GetToken(string issuer, string audience, string keyIdentifier, byte[] tokenVerificationKey)
        {
            var tokenSigningKey = new SymmetricSecurityKey(tokenVerificationKey);

            SigningCredentials cred = new SigningCredentials(
                tokenSigningKey,
                // Use the  HmacSha256 and not the HmacSha256Signature option, or the token will not work!
                SecurityAlgorithms.HmacSha256,
                SecurityAlgorithms.Sha256Digest);

            Claim[] claims = new Claim[]
            {
                new Claim(ContentKeyPolicyTokenClaim.ContentKeyIdentifierClaim.ClaimType, keyIdentifier)
            };

            JwtSecurityToken token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                notBefore: DateTime.Now.AddMinutes(-5),
                expires: DateTime.Now.AddMinutes(60), 
                signingCredentials: cred);

            JwtSecurityTokenHandler handler = new JwtSecurityTokenHandler();
            
            return handler.WriteToken(token);
        }

        private static ContentKeyPolicy EnsureContentKeyPolicyExists(IAzureMediaServicesClient client,
            string resourceGroup, string accountName, string contentKeyPolicyName)
        {
            ContentKeyPolicySymmetricTokenKey primaryKey = new ContentKeyPolicySymmetricTokenKey(TokenSigningKey);
            List<ContentKeyPolicyRestrictionTokenKey> alternateKeys = null;
            List<ContentKeyPolicyTokenClaim> requiredClaims = new List<ContentKeyPolicyTokenClaim>()
            {
                ContentKeyPolicyTokenClaim.ContentKeyIdentifierClaim
            };

            List<ContentKeyPolicyOption> options = new List<ContentKeyPolicyOption>()
            {
                new ContentKeyPolicyOption(
                    new ContentKeyPolicyClearKeyConfiguration(),
                    new ContentKeyPolicyTokenRestriction(Issuer, Audience, primaryKey,
                        ContentKeyPolicyRestrictionTokenType.Jwt, alternateKeys, requiredClaims))
            };

            // since we are randomly generating the signing key each time, make sure to create or update the policy each time.
            // Normally you would use a long lived key so you would just check for the policies existence with Get instead of
            // ensuring to create or update it each time.
            ContentKeyPolicy policy = client.ContentKeyPolicies.CreateOrUpdate(resourceGroup, accountName, contentKeyPolicyName, options);

            return policy;
        }


        private async static Task DownloadResults(IAzureMediaServicesClient client, string resourceGroupName, string accountName,  string assetName, string resultsFolder)
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
