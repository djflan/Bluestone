namespace Bluestone.Domain.Devices;

/// <summary>
/// Finds the definition behind a device reference, or null when it is not available (a plugin that is
/// not installed). The domain never loads implementations; callers supply what they know.
/// </summary>
public delegate DeviceDefinition? DeviceDefinitionLookup(DeviceDefinitionId id);
