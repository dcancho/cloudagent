# Copy this file to  agent.secrets.ps1  (gitignored) and fill in your values.
# run.ps1 dot-sources it before launching. Pick ONE chat provider.

# --- Microsoft Foundry (recommended — you're already on it) ---
$env:FOUNDRY_ENDPOINT    = "https://<resource>.services.ai.azure.com/models"
$env:FOUNDRY_API_KEY     = "<key>"
$env:FOUNDRY_MODEL       = "gpt-4o-mini"             # cheap default; raise per task later
$env:FOUNDRY_EMBED_MODEL = "text-embedding-3-small"

# --- or Anthropic (instead of Foundry) ---
# $env:ANTHROPIC_API_KEY = "sk-ant-..."

# --- with no provider set, the agent runs offline on the echo provider (no real intelligence) ---

# --- Optional surfaces (outbound-only: they work from localhost, no public server) ---
# $env:TELEGRAM_BOT_TOKEN = "..."
# $env:DISCORD_BOT_TOKEN  = "..."

# --- Optional extras ---
# $env:TAVILY_API_KEY             = "..."            # real web search (else web_search is a no-op)
# $env:AGENT_WORKSPACE            = "C:\Users\diego\agent-workspace"  # default: .\workspace
# $env:AGENT_MEMORY_BUDGET_TOKENS = "8000"
# $env:WEBCHAT_PORT               = "5080"
