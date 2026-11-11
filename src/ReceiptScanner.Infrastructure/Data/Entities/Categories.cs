using ReceiptScanner.Core.Domain;

namespace ReceiptScanner.Infrastructure.Data.Entities;

// Mapped to categories/models.py. Auto-categorisation is out of scope for the
// .NET port: these are read and displayed, never computed.

/// <summary>Table <c>categories</c>. Two levels at most.</summary>
public class Category
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long? ParentId { get; set; }
    public Category? Parent { get; set; }
    public string Kind { get; set; } = CategoryKind.Expense;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public List<Category> Children { get; set; } = [];

    /// <summary>"Parent › Name". Parent must be loaded.</summary>
    public string FullName => Parent is null ? Name : $"{Parent.Name} › {Name}";
}

/// <summary>Table <c>category_rules</c>. Read-only in the .NET port.</summary>
public class CategoryRule
{
    public long Id { get; set; }
    public string Pattern { get; set; } = "";
    public string MatchType { get; set; } = "prefix";
    public string AppliesTo { get; set; } = "both";
    public long CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public short Priority { get; set; }
    public string CreatedBy { get; set; } = "manual";
    public bool IsActive { get; set; } = true;
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Django's <c>auth_user</c>. Kept as-is (no ASP.NET Identity tables) so
/// existing users and their pbkdf2_sha256 hashes keep working.
/// </summary>
public class User
{
    public int Id { get; set; }
    /// <summary>Django hash string: <c>pbkdf2_sha256$&lt;iterations&gt;$&lt;salt&gt;$&lt;hash&gt;</c>.</summary>
    public string Password { get; set; } = "";
    public DateTime? LastLogin { get; set; }
    public bool IsSuperuser { get; set; }
    public string Username { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public bool IsStaff { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime DateJoined { get; set; }
}
