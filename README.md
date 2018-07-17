---
services: media-services
platforms: dotnet-core
author: Juliako
---

# Azure Media Services v3 .NET Core tutorials

The projects in this repository were created using Visual Studio Code.

The /NETCore folder contains samples showing how to use Azure Media Services v3 API and .NET SDK in .NET Core.  

Open the each project under the /NETCore folder directly (seperately) in Visual Studio Code in order for the build process to work properly. Failure to do so will result in VS code asking for new configuration and build files to be created. 

## Prerequisites

To run samples in this repository, you need:

* Visual Studio Code
* Microsoft .NET Core SDK v2.1 or newer

## Required assemblies

The following NuGet packages were added to project: 

* Microsoft.Azure.Management.Media. To make sure you are using the latest Azure Media Services package, check [Microsoft.Azure.Management.Media](https://www.nuget.org/packages/Microsoft.Azure.Management.Media).
* Microsoft.IdentityModel.Clients.ActiveDirectory -Version 3.19.4
* WindowsAzure.Storage  -Version 9.1.1

## Update the App.config

To run each application in the /NETCore folder, you must first update the App.config with your account settings. The settings for your account can be retrieved using the following Azure CLI command in the Media Services module.

The following bash shell script creates a service principal for the account and returns the app.config settings as xml

    #!/bin/bash

    resourceGroup=amsResourceGroup
    amsAccountName=amsaccountname
    amsSPName=amsAADapplication

    # Create a service principal with password and configure its access to an Azure Media Services account.
    az ams account sp create \
    --account-name $amsAccountName \
    --name $amsSPName \
    --resource-group $resourceGroup \
    --role Owner \
    --xml \
    --years 2 \

For more information, see [Access APIs](https://docs.microsoft.com/azure/media-services/latest/access-api-cli-how-to).
