using Microsoft.Extensions.DependencyInjection;
using Wabbajack.CLI.Builder;

namespace Wabbajack.CLI;

public static class CommandLineBuilderExtensionsManual
{
	public static void AddCLIVerbs(this IServiceCollection services)
	{
		// CommandLineBuilder.RegisterCommand is static; VerbRegistration only needs IServiceCollection.
		// Provide a dummy provider to satisfy the constructor; it is not used by registration.
		var dummyProvider = new ServiceCollection().BuildServiceProvider();
		var dummyBuilder = new CommandLineBuilder(dummyProvider);
		VerbRegistration.RegisterVerbs(dummyBuilder, services);
	}
}
