using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace DriftGodPlugin;

[ApiController]
[Route("driftgod")]
public class DriftGodController : ControllerBase
{
    [HttpGet("fonts/{fontName}")]
    public IActionResult GetFont(string fontName)
    {
        try
        {
            // Get the plugin directory
            string pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            string fontsPath = Path.Combine(pluginDirectory, "fonts", fontName);
            
            // Check if font file exists
            if (!System.IO.File.Exists(fontsPath))
            {
                // List available fonts for debugging
                string fontsDirectory = Path.Combine(pluginDirectory, "fonts");
                if (Directory.Exists(fontsDirectory))
                {
                    var availableFonts = Directory.GetFiles(fontsDirectory, "*.ttf")
                        .Concat(Directory.GetFiles(fontsDirectory, "*.otf"))
                        .Select(f => Path.GetFileName(f))
                        .ToArray();
                    
                    return BadRequest($"Font '{fontName}' not found at {fontsPath}. Available fonts: {string.Join(", ", availableFonts)}");
                }
                else
                {
                    return BadRequest($"Fonts directory not found at {fontsDirectory}");
                }
            }
            
            // Determine content type based on file extension
            string contentType = fontName.ToLower() switch
            {
                var name when name.EndsWith(".woff") => "font/woff",
                var name when name.EndsWith(".otf") => "font/otf",
                _ => "font/ttf"
            };
            
            return PhysicalFile(fontsPath, contentType, fontName);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error serving font: {ex.Message}");
        }
    }
}