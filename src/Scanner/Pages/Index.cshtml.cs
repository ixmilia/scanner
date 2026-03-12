using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Scanner.Pages;

public class IndexModel : PageModel
{
    public string Message { get; private set; } = string.Empty;

    public void OnGet()
    {
        Message = "Hello, World!";
    }
}
