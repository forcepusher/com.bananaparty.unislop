# com.bananaparty.unislop  
  
Minimalistic Unity MCP server for coding assistants on a Local LLM machine.  
  
This tool is not for enter-button-mashers using Claude Code or something.  
This MCP helps you to avoid copy-pasting errors and test runner results.  
  
Make sure you have the standalone [Git](https://git-scm.com/downloads) installed. Reboot after installation.  
In Unity, open "Window" -> "Package Manager".  
Click the "+" sign at the top left corner -> "Add package from git URL..."  
Paste this: `https://github.com/forcepusher/com.bananaparty.unislop.git#2.1.3`  
See the minimum required Unity version in the `package.json` file.  
To update the package, simply add it again using a different version tag.  
  
The server is started automatically at `http://127.0.0.1:5107/mcp` and your can also enable the status bar.  
At the top right corner of your Unity UI, click three dots -> UniSlop -> Tick on "MCP".  