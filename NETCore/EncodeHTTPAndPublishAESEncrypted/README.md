# Encode HTTP and Publish stream as Envelope(AES) Encrypted
This sample demonstrates how to create an encoding Transform that uses a built-in preset for adaptive bitrate encoding and ingests a file directly from an HTTPs source URL.  This is simpler than having to create an Asset and upload content to it directly for some scenarios.

The output asset is then Published using Envelope (AES), or sometimes referred to as ClearKey, encryption. 
The sample publishes the asset on the "default" StreamingEndpoint, so please make sure that you have already started the default endpoint, or modify the code to start the default or create a new Streaming Endpoint and autostart it. 

The output from the sample is a URL to the Azure Media Player, including both the DASH Manifest and the AES Token needed to play back the content.
The sample sets the expiration of the JWT token to 1 hour.

The URL output to the console takes the following format.  You can open a browser and paste the resulting URL to launch the Azure Media Player demo page with the URL and Token filled out for you already. 
    https://ampdemo.azureedge.net/?url= {dash Manifest URL} &aes=true&aestoken=Bearer%3D{ JWT Token here}

## .NET Core Sample

This is a Quickstart sample showing how to use Azure Media Services API and .NET SDK in .NET Core. 
Open this folder directly (seperately) in Visual Studio Code. 

## Required Assemblies in the project
- Microsoft.Azure.Management.Media -Version 2.0.1
- WindowsAzure.Storage  -Version 9.1.1
- Microsoft.Rest.ClientRuntime.Azure.Authentication -Version 2.3.3

## Update the appsettings.json

To use this project, you must first update the appsettings.json with your account settings. The settings for your account can be retrieved using the following Azure CLI command in the Media Services module.
The following bash shell script creates a service principal for the account and returns the json settings.

    #!/bin/bash

    resourceGroup=build2018
    amsAccountName=build2018
    amsSPName=build2018AADapplication

    # Create a service principal with password and configure its access to an Azure Media Services account.
    az ams account sp create \
    --account-name $amsAccountName \
    --name $amsSPName \
    --resource-group $resourceGroup \
    --role Owner \
    --years 2 \
