using System.Text.Json;
using ScadaDarbox.Core.Security;

namespace ScadaDarbox.Gateway.Contracts;

public sealed record LoginRequest(string? Username, string? Password);

public sealed record SiteRoleDto(Guid SiteId, string Role);

/// <summary>What the signed-in user may do, so the client can show only what will work.</summary>
/// <remarks>
/// A convenience for the UI, never the enforcement: every request is checked on the server
/// regardless of what the client decided to show.
/// </remarks>
public sealed record AccessDto(Guid UserId, string Username, bool IsAdmin, IReadOnlyList<SiteRoleDto> Sites)
{
    public static AccessDto From(UserAccess access) => new(
        access.UserId,
        access.Username,
        access.IsAdmin,
        access.SiteRoles
            .Select(pair => new SiteRoleDto(pair.Key, pair.Value.ToString()))
            .OrderBy(role => role.SiteId)
            .ToList());
}

/// <param name="Token">The only copy of the session token. The server keeps just its hash.</param>
public sealed record LoginResponse(string Token, AccessDto Access);

public sealed record CreateUserRequest(string? Username, string? Password, bool IsAdmin);

public sealed record SetAdminRequest(bool IsAdmin);

public sealed record SetPasswordRequest(string? Password);

public sealed record SetSiteRoleRequest(string? Role);

/// <summary>
/// A value to write to a tag. Interpreted by the tag's own kind — a number, true/false, a
/// string, or an integer code — so the client cannot claim a kind the tag does not have.
/// </summary>
public sealed record WriteTagValueRequest(JsonElement Value);
