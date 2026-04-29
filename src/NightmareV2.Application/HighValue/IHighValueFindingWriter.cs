namespace NightmareV2.Application.HighValue;

public interface IHighValueFindingWriter
{
    Task<Guid> InsertFindingAsync(HighValueFindingInput input, CancellationToken cancellationToken = default);
}
