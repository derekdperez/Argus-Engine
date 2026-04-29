using MassTransit;

namespace NightmareV2.Workers.Enum.Consumers;

public sealed class SubdomainEnumerationRequestedConsumerDefinition : ConsumerDefinition<SubdomainEnumerationRequestedConsumer>
{
    public SubdomainEnumerationRequestedConsumerDefinition()
    {
        EndpointName = "subdomain-enumeration";
        ConcurrentMessageLimit = 8;
    }
}
