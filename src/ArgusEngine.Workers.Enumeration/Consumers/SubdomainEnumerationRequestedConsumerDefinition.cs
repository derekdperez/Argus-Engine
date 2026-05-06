using MassTransit;

namespace ArgusEngine.Workers.Enumeration.Consumers;

public sealed class SubdomainEnumerationRequestedConsumerDefinition : ConsumerDefinition<SubdomainEnumerationRequestedConsumer>
{
    public SubdomainEnumerationRequestedConsumerDefinition()
    {
        EndpointName = "subdomain-enumeration";
        ConcurrentMessageLimit = 8;
    }
}
