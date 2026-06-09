# Deploy to Azure

The solution consists of three services:

- **Proxy** — The OpenAI-compatible API proxy (Container App)
- **Admin** — The management UI for events and resources (Container App, Entra ID authentication)
- **Registration** — The attendee registration SPA (Static Web App)

## Setup

This repo is set up for deployment on Azure Container Apps using the configuration files in the `infra` folder.

### Prerequisites

1. An Azure subscription
2. The Azure CLI logged in to a tenant where you have permission to create Entra ID app registrations
3. An Azure region that supports the AI models you plan to deploy (models are deployed after `azd up`)

### Required permissions

| Requirement | Why |
|---|---|
| **Azure subscription** | To provision Container Apps, Storage Account, Container Registry, and Static Web App |
| **Entra ID app registration** | The deployment automatically creates an app registration for admin UI authentication. You need permission to create app registrations in your tenant (typically requires at least the `Application Developer` role) |
| **Correct tenant** | If you have multiple tenants, ensure `az account show` points to the correct one before running `azd up`. Use `az login --tenant <tenant-id>` to switch tenants |

### Required software

Tested on Windows, macOS and Ubuntu 22.04.

Install:

1. [VS Code](https://code.visualstudio.com/)
2. [VS Code Dev Containers extension](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers)
3. [Docker](https://www.docker.com/products/docker-desktop)

## Deploying

The recommended way to deploy this app is with Dev Containers. Install the [VS Code Remote Containers extension](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers) and Docker, open this repository in a container and you'll be ready to go.

1. Ensure Docker is installed and running.
2. Clone the repo:

    ```shell
    git clone https://github.com/microsoft/azure-ai-proxy-lite.git
    ```

3. Open the repo in VS Code.
4. You will be prompted to `Reopen in Container`, click the button to do so. This will build the container and open the repo in a container.
5. In the VS Code dev container, open a terminal and set your Azure tenant and subscription IDs once:

    ```shell
    export AZURE_TENANT_ID=<your-tenant-id>
    export AZURE_SUBSCRIPTION_ID=<your-subscription-id>
    ```

6. Log in to both `azd` and the Azure CLI (`az`). Both are required because `azd` deploys the infrastructure while the deployment hooks use `az` to create the Entra ID app registration:

    ```shell
    azd auth login --tenant-id "$AZURE_TENANT_ID" --use-device-code
    azd config set defaults.subscription "$AZURE_SUBSCRIPTION_ID"

    az login --tenant "$AZURE_TENANT_ID" --use-device-code
    az account set --subscription "$AZURE_SUBSCRIPTION_ID"
    ```

7. Set the encryption key for this `azd` environment. The proxy uses this key to encrypt sensitive resource configuration, including backend endpoint URLs and API keys stored by the admin app:

    ```shell
    azd env set ENCRYPTION_KEY "$(openssl rand -hex 32)"
    ```

    Do not change `ENCRYPTION_KEY` after resources have been added in the admin UI. Existing encrypted catalog entries were encrypted with the previous key and may become unreadable if the key changes.

8. Provision and deploy the proxy solution:

    ```shell
    azd up
    ```

    You will be prompted for:

    1. **Environment name** — keep it short (max 7 characters) to avoid invalid resource names.
    2. **Azure subscription** — select from your Azure account.
    3. **Location** — e.g., "centralus" or "eastus".
    4. **swaLocation** — location for the Static Web App (choose from the allowed list). Recommend deploying in the same location as the proxy.
    5. **foundryLocation** — location for the Azure AI Foundry project and model deployments.

    The deployment will automatically:

    - Create an Entra ID app registration for admin authentication
    - Provision all Azure resources (Container Apps, Storage, Registry, etc.)
    - Create an empty Azure AI Foundry project for you to deploy models into
    - Grant the proxy managed identity access to the Foundry AI Services (no manual RBAC setup needed)
    - Register the OIDC redirect URI for the admin container
    - Configure the admin container with Entra ID env vars
    - Build and deploy all three services

    On completion, the following Azure resources will be provisioned:

    ![Azure resources](../media/azure_resources.png)

9. When `azd` has finished deploying you'll see the endpoints and Entra app registration details in the terminal.

## Authenticating with the AI Proxy Admin

The admin UI uses **Microsoft Entra ID (Azure AD)** authentication when deployed to Azure.

1. Navigate to the admin UI URL displayed after `azd up` completes (or run `azd env get-value SERVICE_ADMIN_URI`).
2. You will be redirected to the Microsoft login page.
3. Sign in with your organizational account.
4. You'll be taken to the admin dashboard.

> **Note:** The admin UI is accessible to any user in the Entra ID tenant where the app registration was created. For production use, consider restricting access using Azure AD Conditional Access policies or app role assignments.

## Updating the deployed app

To make any changes to the app code, just run:

```shell
azd deploy [proxy | admin | registration]
```

The `proxy` service deploys the proxy API. The `admin` service deploys the admin management UI. The `registration` service deploys the attendee registration static web app.

To redeploy everything:

```shell
azd up
```

## Next steps

1. [Deploy Azure AI Resources](#deploy-azure-ai-resources)
1. [Map AI Resources to the AI Proxy](../resources.md)
1. [Create and manage events](../events.md)
1. [Capacity planning](../capacity.md)

## Deploy Azure AI Resources

1. The deployment creates an empty **Azure AI Foundry project** in your resource group. The Foundry project is deployed as a trusted service, and the proxy's managed identity is automatically granted access to the Foundry AI Services — no manual RBAC configuration is needed. Open the [Azure AI Foundry portal](https://ai.azure.com) and deploy models (e.g., GPT-4.1, GPT-4.1-mini) into this project.
2. The proxy supports model deployments from `Azure OpenAI Service`, `Azure AI Foundry Projects`, `MCP Servers`, and `Azure AI Search`.
3. Make a note of the `endpoint_key` and `endpoint_url` as you'll need them when you configure resources for the AI Proxy.
4. For Managed Identity deployments, see the [Managed Identity guide](managed_identity.md).

## Troubleshooting

If you encounter any issues deploying the solution, please raise an issue on the [GitHub repo](https://github.com/microsoft/azure-ai-proxy-lite/issues)
