# Clawcker - OpenClaw Instance Manager

Clawcker is a subcommand of Chimpiler that makes it trivially easy to create, run, and access local OpenClaw instances using Docker.

## Prerequisites

- Docker must be installed and running
- Docker daemon must be accessible

## Quick Start

Create, start, and access an OpenClaw instance with three commands:

```bash
chimpiler clawcker new myagent
chimpiler clawcker start myagent
chimpiler clawcker talk myagent
```

## Commands

### `chimpiler clawcker new <name>`

Creates a new OpenClaw instance with the specified name.

**What it does:**
- Checks that Docker is installed and running
- Pulls the latest OpenClaw Docker image (`ghcr.io/phioranex/openclaw-docker:latest`)
- Creates a configuration directory at `./.clawcker/<name>/config`
- Creates a workspace directory at `./.clawcker/<name>/workspace`
- Allocates a unique port for this instance
- Generates a secure random gateway authentication token
- Saves instance metadata

### `chimpiler clawcker start <name>`

Starts an OpenClaw instance.

**What it does:**
- Checks if the instance exists
- Creates and starts a Docker container with:
  - The instance's configuration and workspace mounted as volumes
  - The gateway token set as an environment variable
  - The instance's unique port exposed for the web UI
  - Automatic restart enabled
- Starts the OpenClaw gateway

### `chimpiler clawcker talk <name>`

Opens the OpenClaw web UI for an instance in your default browser.

**What it does:**
- Checks if the instance exists
- Verifies the container is running
- Opens the web UI URL (with authentication token) in your default browser

### `chimpiler clawcker list`

Lists all Clawcker instances.

**What it does:**
- Scans the `./.clawcker/` directory for instances
- Checks the status of each instance's Docker container
- Displays instance details including status, port, and creation time

### `chimpiler clawcker stop <name>`

Stops a running OpenClaw instance.

**What it does:**
- Checks if the instance exists
- Stops the Docker container (but does not remove it)
- The instance can be restarted with `start`

## Instance Storage

Instances are stored in `./.clawcker/<name>/` (relative to current working directory):
- `config/` - OpenClaw configuration files
- `workspace/` - Agent workspace files
- `instance.json` - Instance metadata (name, port, token, etc.)

This allows you to commit the `.clawcker/` directory to version control and manage your instances alongside your project.

## Port Allocation

Each instance automatically receives its own unique port, starting from 18789. When you create a new instance, Clawcker finds the next available port to avoid conflicts. The `talk` command automatically uses the correct port for each instance.

## Security

- Each instance has a unique, randomly generated 256-bit gateway authentication token
- The token is required to access the web UI
- Tokens are stored locally in the instance metadata file

## Docker Container Details

- **Image:** `ghcr.io/phioranex/openclaw-docker:latest`
- **Container name:** `clawcker-<instance-name>`
- **Volumes:**
  - `./.clawcker/<name>/config` → `/home/node/.openclaw`
  - `./.clawcker/<name>/workspace` → `/home/node/.openclaw/workspace`
- **Environment variables:**
  - `OPENCLAW_GATEWAY_TOKEN` - Authentication token
- **Restart policy:** unless-stopped

## Troubleshooting

### Docker not found
**Error:** Docker is not installed. Please install Docker from https://www.docker.com/get-started

**Solution:** Install Docker Desktop or Docker Engine.

### Docker daemon not running
**Error:** Docker daemon is not running. Please start Docker and try again

**Solution:** Start Docker Desktop or the Docker daemon.

### Instance already exists
**Error:** Instance 'myagent' already exists

**Solution:** Use a different name or remove the existing instance directory.

### Instance not running
**Error:** Instance 'myagent' is not running. Start it first with 'chimpiler clawcker start myagent'

**Solution:** Start the instance with `chimpiler clawcker start myagent`.

## Examples

### Create and start a new instance
```bash
chimpiler clawcker new myagent
chimpiler clawcker start myagent
chimpiler clawcker talk myagent
```

### List all instances
```bash
chimpiler clawcker list
```

### Stop an instance
```bash
chimpiler clawcker stop myagent
```

### Restart a stopped instance
```bash
chimpiler clawcker start myagent
```

## Notes

- Multiple instances can run simultaneously, each on its own port
- The web UI will guide you through the OpenClaw onboarding process on first access
- Instance data is stored in the current working directory, making it easy to version control
