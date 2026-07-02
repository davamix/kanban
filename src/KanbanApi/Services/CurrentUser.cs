using System.Security.Claims;

namespace KanbanApi.Services;

/// <summary>
/// Claims-based <see cref="ICurrentUser"/>. Reads the raw <c>sub</c> (both schemes set
/// <c>MapInboundClaims = false</c>) with a fallback to the mapped NameIdentifier.
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public string? Id =>
        Principal?.FindFirst("sub")?.Value
        ?? Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public string? Email =>
        Principal?.FindFirst("email")?.Value
        ?? Principal?.FindFirst(ClaimTypes.Email)?.Value;

    public string? DisplayName =>
        Principal?.FindFirst("name")?.Value
        ?? Principal?.FindFirst("username")?.Value
        ?? Email;
}
