using ClubShell.Server.Infrastructure;

namespace ClubShell.Server.Users;

/// <summary>A player's gender (D-104): <c>male</c> or <c>female</c> (<c>users.gender</c>, M0029).</summary>
public static class Genders
{
    /// <summary>The given gender, else <c>400 gender required</c> (none) or <c>400 gender enum</c> (anything else).</summary>
    public static string Required(string? gender) => gender switch
    {
        null or "" => throw ApiException.Validation("gender", "required"),
        "male" or "female" => gender,
        _ => throw ApiException.Validation("gender", "enum"),
    };
}
