# Docker Deployment

For LAN or offline deployments (e.g., workshops without Azure), you can run the full stack locally using Docker Compose with Azurite (an open-source Azure Table Storage emulator). The local deployment uses **username/password authentication** instead of Entra ID.

## Prerequisites

- Docker and Docker Compose installed
- The server must be reachable from attendee machines on the LAN

## Setup

1. Navigate to the `docker/` folder and copy the example environment file:

    ```bash
    cd docker
    cp .env.example .env
    ```

2. Edit `.env` and update the following:

    **`REGISTRATION_HOST`** — Set to your server's hostname or LAN IP address. The admin UI generates attendee registration links using this value. If left as `localhost`, links will only work from the server itself.

    ```env
    REGISTRATION_HOST=192.168.1.50
    ```

    **`ENCRYPTION_KEY`** — Used to encrypt API keys and deployment credentials stored in Azurite. Generate a secure random key:

    ```bash
    openssl rand -base64 32
    ```

    Paste the output into `.env`:

    ```env
    ENCRYPTION_KEY=<paste-your-generated-key-here>
    ```

    > **Important:** If you change this key after data has been written, existing encrypted data becomes unreadable.

    **`ADMIN_PASSWORD`** — Change the default admin password:

    ```env
    ADMIN_PASSWORD=your-secure-password
    ```

3. Start the stack:

    ```bash
    docker compose up -d
    ```

## Accessing the services

| Service | URL |
|---|---|
| Admin UI | `http://<your-host>:8900` |
| Proxy API | `http://<your-host>:8910/api/v1` |
| Registration | `http://<your-host>:4280/event/<event-id>` |

Sign in to the admin UI with the `ADMIN_USERNAME` and `ADMIN_PASSWORD` from your `.env` file (default username is `admin`).

For the full local deployment reference (managing the stack, building custom images, multi-architecture builds), see the [Docker deployment guide](https://github.com/microsoft/azure-ai-proxy-lite/blob/main/docker/README.md){:target="_blank"}.

## Managing the stack

### Stopping the containers

```bash
docker compose down
```

### Pulling fresh images and restarting

To update to the latest published images, pull them first then recreate the containers:

```bash
docker compose down && docker compose pull && docker compose up -d
```

### Removing containers and volumes

Use the `-v` flag to remove named volumes along with the containers. This deletes all persistent data including the Azurite storage (events, attendees, API keys, resources):

```bash
docker compose down -v
```

!!! warning
    The `-v` flag removes all data stored in Docker volumes. You will need to reconfigure events, resources, and attendees from scratch.

### Viewing logs

To follow the logs for all services:

```bash
docker compose logs -f
```

To view logs for a specific service:

```bash
docker compose logs -f proxy
```

## Next steps

1. [Map AI Resources to the AI Proxy](../resources.md)
1. [Create and manage events](../events.md)
1. [Capacity planning](../capacity.md)
