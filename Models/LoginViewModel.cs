using System.ComponentModel.DataAnnotations;

namespace H2HClientWeb.Models;

public sealed class LoginViewModel
{
    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = "";
    public string? Error { get; set; }
}
