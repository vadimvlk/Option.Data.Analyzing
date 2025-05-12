using System.ComponentModel.DataAnnotations;

namespace Option.Data.Shared.Poco;

// ReSharper disable InconsistentNaming
public class CurrencyType
{
    public int Id { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string Name { get; set; }
    
    // Navigation property
    public virtual ICollection<OptionData> Options { get; set; }
}
