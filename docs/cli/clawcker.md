# Clawcker - OpenClaw Instance Manager

Clawcker is a subcommand of Chimpiler that makes it trivially easy to create, run, and access local OpenClaw instances using Docker.

## Prerequisites

- Docker must be installed and running
- Docker daemon must be accessible

## Quick Start

Create, start, and access an OpenClaw instance with three commands:

```bash
chimpiler clawcker new myagent
chimpiler clawcker up myagent
chimpiler clawcker talk myagent
```

## Commands

### `chimpiler clawcker new <name>`

Creates a new OpenClaw instance with the specified name.

**What it does:**
- Checks that Docker is installed and running
- Pulls the latest OpenClaw Docker image (`ghcr.io/phioranex/openclaw-docker:latest`)
- Creates a configuration directory at `~/.chimpiler/clawcker/<name>/config`
- Creates a workspace directory at `~/.chimpiler/clawcker/<name>/workspace`
- Generates a secure random gateway authentication token
- Saves instance metadata

**Example:**
```bash
chimpiler clawcker new myagent
```

**Output:**
```
Creating new Clawcker instance: myagent
Pulling OpenClaw Docker image...
(This may take a few minutes on first run)

--- Docker Output ---
[Docker pull output...]
--- End Docker Output ---

✓ Instance 'myagent' created successfully
  Configuration: /home/user/.chimpiler/clawcker/myagent/config
  Workspace: /home/user/.chimpiler/clawcker/myagent/workspace

Next steps:
  1. Run 'chimpiler clawcker up myagent' to start the instance
  2. Run 'chimpiler clawcker talk myagent' to open the web UI
```

### `chimpiler clawcker up <name>`

Starts an OpenClaw instance.

**What it does:**
- Checks if the instance exists
- Creates and starts a Docker container with:
  - The instance's configuration and workspace mounted as volumes
  - The gateway token set as an environment variable
  - Port 18789 exposed for the web UI
  - Automatic restart enabled
- Starts the OpenClaw gateway

**Example:**
```bash
chimpiler clawcker up myagent
```

**Output:**
```
Starting Clawcker instance: myagent
Creating and starting container...
✓ Instance 'myagent' is now running
  Access the web UI at: http://localhost:18789/?token=abc123...
  Container name: clawcker-myagent

To open the web UI in your browser, run:
  chimpiler clawcker talk myagent
```

### `chimpiler clawcker talk <name>`

Opens the OpenClaw web UI for an instance in your default browser.

**What it does:**
- Checks if the instance exists
- Verifies the container is running
- Opens the web UI URL (with authentication token) in your default browser

**Example:**
```bash
chimpiler clawcker talk myagent
```

**Output:**
```
Opening web UI for instance 'myagent'...
URL: http://localhost:18789/?token=abc123...
✓ Web UI opened in your default browser
```

### `chimpiler clawcker list`

Lists all Clawcker instances.

**What it does:**
- Scans the `~/.chimpiler/clawcker/` directory for instances
- Checks the status of each instance's Docker container
- Displays instance details

**Example:**
```bash
chimpiler clawcker list
```

**Output:**
```
Clawcker Instances:

  myagent
    Status: running
    Port: 18789
    Created: 2026-01-31 17:00:00 UTC

  testagent
    Status: stopped
    Port: 18789
    Created: 2026-01-31 16:00:00 UTC
```

### `chimpiler clawcker down <name>`

Stops a running OpenClaw instance.

**What it does:**
- Checks if the instance exists
- Stops the Docker container (but does not remove it)
- The instance can be restarted with `up`

**Example:**
```bash
chimpiler clawcker down myagent
```

**Output:**
```
Stopping Clawcker instance: myagent
Stopping container...
✓ Instance 'myagent' stopped
To start it again, run: chimpiler clawcker up myagent
```

## Instance Storage

Instances are stored in `~/.chimpiler/clawcker/<name>/`:
- `config/` - OpenClaw configuration files
- `workspace/` - Agent workspace files
- `instance.json` - Instance metadata (name, port, token, etc.)

## Security

- Each instance has a unique, randomly generated 256-bit gateway authentication token
- The token is required to access the web UI
- Tokens are stored locally in the instance metadata file

## Docker Container Details

- **Image:** `ghcr.io/phioranex/openclaw-docker:latest`
- **Container name:** `clawcker-<instance-name>`
- **Port:** 18789 (mapped to host)
- **Volumes:**
  - `~/.chimpiler/clawcker/<name>/config` → `/home/node/.openclaw`
  - `~/.chimpiler/clawcker/<name>/workspace` → `/home/node/.openclaw/workspace`
- **Environment variables:**
  - `OPENCLAW_GATEWAY_TOKEN` - Authentication token
- **Restart policy:** unless-stopped

## Troubleshooting

### Docker not found
```
Error: Docker is not installed. Please install Docker from https://www.docker.com/get-started
```
**Solution:** Install Docker Desktop or Docker Engine.

### Docker daemon not running
```
Error: Docker daemon is not running. Please start Docker and try again
```
**Solution:** Start Docker Desktop or the Docker daemon.

### Instance already exists
```
Error: Instance 'myagent' already exists
```
**Solution:** Use a different name or delete the existing instance.

### Instance not running
```
Error: Instance 'myagent' is not running. Start it first with 'chimpiler clawcker up myagent'
```
**Solution:** Start the instance with `chimpiler clawcker up myagent`.

## Examples

### Create and start a new instance
```bash
chimpiler clawcker new myagent
chimpiler clawcker up myagent
chimpiler clawcker talk myagent
```

### List all instances
```bash
chimpiler clawcker list
```

### Stop an instance
```bash
chimpiler clawcker down myagent
```

### Restart a stopped instance
```bash
chimpiler clawcker up myagent
```

## Notes

- Only one instance can run at a time on the default port (18789)
- To run multiple instances simultaneously, you would need to modify the port configuration (not currently supported in v1)
- The web UI will guide you through the OpenClaw onboarding process on first access
