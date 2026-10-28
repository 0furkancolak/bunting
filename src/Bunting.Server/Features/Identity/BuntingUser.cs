using Microsoft.AspNetCore.Identity;

namespace Bunting.Server.Features.Identity;

public sealed class BuntingUser : IdentityUser
{
    /// <summary>Instance administrators act as owners of every project and manage users and projects.</summary>
    public bool IsInstanceAdmin { get; set; }
}
