# com.bananaparty.unislop  
  
Minimalistic Unity MCP server designed for coding on a Local LLM machine.  
  
This MCP automates reporting compilation errors and test runner results to an LLM.  
There are only 3 tools: `unity_compile`, `unity_list_tests` and `unity_run_tests`.  
Those 3 tools are enough to give you ~3x coding speed boost by eliminating copy-pasting routine.  
It will not help you build your game by mashing the enter button - it will only help you code.  
  
Make sure you have the standalone [Git](https://git-scm.com/downloads) installed. Reboot after installation.  
In Unity, open "Window" -> "Package Manager".  
Click the "+" sign at the top left corner -> "Add package from git URL..."  
Paste this: `https://github.com/forcepusher/com.bananaparty.unislop.git#2.1.5`  
See the minimum required Unity version in the `package.json` file.  
To update the package, simply add it again using a different version tag.  
  
The server is started automatically at `http://127.0.0.1:5107/mcp`
Ships with a convenient status bar that displays server status and MCP connection status.  
At the top right corner of your Unity UI, click three dots -> UniSlop -> Tick on "MCP".  
  
Cursor MCP config (requires [Node.js](https://nodejs.org/en/download) installed):  
```
{
  "mcpServers": {
    "unity-mcp": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://127.0.0.1:5107/mcp", "--transport", "http-only", "--allow-http"],
      "env": {}
    }
  }
}
```