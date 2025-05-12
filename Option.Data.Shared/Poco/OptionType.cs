using System.ComponentModel.DataAnnotations;
using JetBrains.Annotations;

namespace Option.Data.Shared.Poco;
[UsedImplicitly]
public class OptionType
{
    public int Id { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string Name { get; set; }
    
    // Navigation property
    public virtual ICollection<DeribitData> Options { get; set; }
}
