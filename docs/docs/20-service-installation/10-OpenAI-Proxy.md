# Azure AI Proxy Service

The solution consists of three services: the proxy API, the admin management UI, and the attendee registration app.

## Setup

This repo is set up for deployment on Azure Container Apps using the configuration files in the `infra` folder.

### Prerequisites

1. An Azure subscription
2. Deployed Azure AI models (OpenAI, Foundry, etc.)
3. Permission to create Entra ID (Azure AD) app registrations in your tenant

### Required software

Tested on Windows, macOS and Ubuntu 22.04.

Install:

1. [VS Code](https://code.visualstudio.com/)
2. [VS Code Dev Containers extension](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers)
3. [Docker](https://www.docker.com/products/docker-desktop)

## Deploying

The recommended way to deploy this app is with Dev Containers. Install the [VS Code Remote Containers extension](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers) and Docker, open this repository in a container and you'll be ready to go.

1. Clone the repo:

    ```shell
    git clone https://github.com/microsoft/azure-ai-proxy-lite.git
    ```

1. Open the repo in VS Code. You will be prompted to `Reopen in Container`, click the button to do so.

1. In the VS Code dev container, open a terminal and authenticate with Azure:

    ```shell
    azd auth login --use-device-code
    ```

    If you have **multiple tenants**, also log in with the correct tenant:

    ```shell
    az login --tenant <your-tenant-id> --use-device-code
    ```

1. Provision and deploy all the resources:

    ```shell
    azd up
    ```

    You will be prompted for the following:

    1. The environment name, keep the name short, max 7 characters to avoid invalid resource names being generated.
    2. Select a subscription from your Azure account.
    3. Select a location (like "eastus" or "sweden central"). Recommend deploying the proxy to the same location you plan to deploy your models.
    4. Select the 'swaLocation' infrastructure parameter.

    The deployment automatically creates an Entra ID app registration, provisions all resources, configures OIDC authentication on the admin UI, and deploys all three services.

    On completion, the following Azure resources will be provisioned:

    ![Azure resources](../media/azure_resources.png)

1. When `azd` has finished deploying you'll see the endpoints and Entra ID app registration details in the terminal.

1. Open the **Admin UI** URL and sign in with your Microsoft account.

1. To make any changes to the app code, just run:

    ```shell
    azd deploy [proxy | admin | registration]
    ```
