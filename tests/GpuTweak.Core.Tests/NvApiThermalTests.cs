using GpuTweak.Core.Backends;

namespace GpuTweak.Core.Tests;

public sealed class NvApiThermalTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 5080", 2)]
    [InlineData("NVIDIA GeForce RTX 4090", 7)]
    [InlineData("NVIDIA GeForce RTX 3080", 9)]
    public void MemorySensorSlotFollowsTheGeneration(string name, int slot) => Assert.Equal(slot, NvApiThermal.SlotFor(name));
}
