CLAWCKER - OpenClaw Instance Manager
================================================================================

Clawcker makes it easy to create, run, and access local OpenClaw instances
using Docker.

PREREQUISITES
  - Docker must be installed and running
  - Docker daemon must be accessible

QUICK START
  $ chimpiler clawcker new myagent
  $ chimpiler clawcker talk myagent

  The 'new' command prompts for your AI provider and API key, then
  automatically starts the instance.

COMMANDS
--------------------------------------------------------------------------------

  new <name>           Create a new OpenClaw instance
  configure <name>     Change provider/model for an existing instance
  start <name>         Start an instance
  stop <name>          Stop a running instance
  talk <name>          Open the web UI in your browser
  list                 List all instances
  health <name>        Check if an instance is healthy

COMMAND DETAILS
--------------------------------------------------------------------------------

new <name>
  Creates a new OpenClaw instance with the specified name.

  Options:
    -p, --provider <provider>   AI provider: anthropic, openai, openrouter, gemini
    -k, --api-key <key>         API key for the provider

  If options are omitted, you'll be prompted interactively.

  Default models by provider:
    anthropic     anthropic/claude-sonnet-4
    openai        openai/gpt-5.2
    openrouter    openrouter/anthropic/claude-sonnet-4
    gemini        google-gemini/gemini-2.5-pro

  What it does:
    - Pulls the OpenClaw Docker image
    - Creates config at ./.clawcker/<name>/config
    - Creates workspace at ./.clawcker/<name>/workspace
    - Allocates a unique port (starting from 18789)
    - Generates a secure gateway token
    - Configures credentials and default model
    - Automatically starts the instance

configure <name>
  Reconfigure an existing instance with a new provider/model.

  Options:
    -p, --provider <provider>   AI provider: anthropic, openai, openrouter, gemini
    -k, --api-key <key>         API key for the provider

  Example:
    $ chimpiler clawcker configure myagent --provider openai --api-key sk-...

start <name>
  Start an OpenClaw instance. Creates the Docker container if needed.

stop <name>
  Stop a running instance. The container is stopped but not removed.

talk <name>
  Open the web UI in your default browser (includes auth token).

list
  List all instances with status, port, and creation time.

health <name>
  Check container status and test if the gateway is responding.

INSTANCE STORAGE
--------------------------------------------------------------------------------

Instances are stored in ./.clawcker/<name>/:

  config/         OpenClaw configuration files
  workspace/      Agent workspace files
  instance.json   Instance metadata (name, port, token, provider)

This allows version control of the .clawcker/ directory with your project.

DOCKER DETAILS
--------------------------------------------------------------------------------

  Image:       ghcr.io/phioranex/openclaw-docker:latest
  Container:   clawcker-<instance-name>
  Restart:     unless-stopped

  Volumes:
    ./.clawcker/<name>/config    -> /home/node/.openclaw
    ./.clawcker/<name>/workspace -> /home/node/.openclaw/workspace

  Environment:
    OPENCLAW_GATEWAY_TOKEN       Gateway auth token
    OPENAI_API_KEY               (if using OpenAI)
    ANTHROPIC_API_KEY            (if using Anthropic)
    OPENROUTER_API_KEY           (if using OpenRouter)
    GOOGLE_API_KEY               (if using Gemini)

SECURITY
--------------------------------------------------------------------------------

Each instance has a unique 256-bit gateway token stored in instance.json.
The token is required to access the web UI.

EXAMPLES
--------------------------------------------------------------------------------

  Create and access a new instance:
    $ chimpiler clawcker new myagent
    $ chimpiler clawcker talk myagent

  Create with specific provider:
    $ chimpiler clawcker new myagent -p openai -k sk-...

  Switch provider:
    $ chimpiler clawcker configure myagent -p anthropic

  List instances:
    $ chimpiler clawcker list

  Stop/restart:
    $ chimpiler clawcker stop myagent
    $ chimpiler clawcker start myagent

  Check health:
    $ chimpiler clawcker health myagent

TROUBLESHOOTING
--------------------------------------------------------------------------------

  "Docker is not installed"
    Install Docker Desktop or Docker Engine from https://docker.com/get-started

  "Docker daemon is not running"
    Start Docker Desktop or the Docker daemon service.

  "Instance 'name' already exists"
    Use a different name or remove ./.clawcker/<name>/

  "Instance 'name' is not running"
    Run: chimpiler clawcker start <name>

NOTES
--------------------------------------------------------------------------------

  - Multiple instances can run simultaneously on different ports
  - Instances auto-start after creation
  - Use 'configure' to switch providers without recreating
  - Port allocation starts at 18789 and auto-increments
