namespace UIBlazor.Options;

public class ProfileOptions : BaseOptions
{
    public List<ConnectionProfile> Profiles { get; set => SetIfChanged(ref field, value); } = [];
    
    public string? ActiveProfileId { get; set => SetIfChanged(ref field, value); }
}
