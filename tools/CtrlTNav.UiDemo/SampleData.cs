using System;
using System.Collections.Generic;
using System.IO;
using CtrlTNav.Core.Model;

namespace CtrlTNav.UiDemo
{
    /// <summary>
    /// A small but deliberately varied slice of a fictional Vulkan/C++ code base. The names are chosen so a
    /// typical query ("device") produces interesting matches across every kind still rendered by the popup.
    /// </summary>
    internal static class SampleData
    {
        public const string BaseDirectory = @"C:\Projects\VulkanEngine";

        private const string EngineProject = "Engine";
        private const string RenderProject = "Renderer";
        private const string ThirdPartyProject = "ThirdParty";

        public static IReadOnlyList<NavigationItem> CreateItems()
        {
            var items = new List<NavigationItem>();

            // ----- namespaces ----------------------------------------------------------------
            Add(items, "Engine", ItemKind.Namespace, @"Engine", "Engine.vcxproj", 0, EngineProject, "");
            Add(items, "Render", ItemKind.Namespace, @"Engine\Render", "Render.h", 6, RenderProject, "Engine");
            Add(items, "Vulkan", ItemKind.Namespace, @"Engine\Render\Vulkan", "Vulkan.h", 8, RenderProject, "Engine::Render");
            Add(items, "DeviceApi", ItemKind.Namespace, @"Engine\Render\Vulkan", "DeviceApi.h", 11, RenderProject, "Engine::Render::Vulkan");

            // ----- classes / structs / enums -------------------------------------------------
            Add(items, "VulkanDevice", ItemKind.Class, @"Engine\Render\Vulkan", "VulkanDevice.h", 18, RenderProject, "Engine::Render::Vulkan");
            Add(items, "VulkanInstance", ItemKind.Class, @"Engine\Render\Vulkan", "VulkanInstance.h", 14, RenderProject, "Engine::Render::Vulkan");
            Add(items, "VulkanPhysicalDevice", ItemKind.Class, @"Engine\Render\Vulkan", "VulkanPhysicalDevice.h", 21, RenderProject, "Engine::Render::Vulkan");
            Add(items, "VulkanSwapchain", ItemKind.Class, @"Engine\Render\Vulkan", "VulkanSwapchain.h", 24, RenderProject, "Engine::Render::Vulkan");
            Add(items, "VulkanQueue", ItemKind.Class, @"Engine\Render\Vulkan", "VulkanQueue.h", 11, RenderProject, "Engine::Render::Vulkan");
            Add(items, "VulkanAllocator", ItemKind.Class, @"Engine\Render\Vulkan", "VulkanAllocator.h", 19, RenderProject, "Engine::Render::Vulkan");
            Add(items, "VulkanImage", ItemKind.Class, @"Engine\Render\Vulkan", "VulkanImage.h", 16, RenderProject, "Engine::Render::Vulkan");
            Add(items, "VulkanPipeline", ItemKind.Class, @"Engine\Render\Vulkan", "VulkanPipeline.h", 27, RenderProject, "Engine::Render::Vulkan");
            Add(items, "VulkanCommandBuffer", ItemKind.Class, @"Engine\Render\Vulkan", "VulkanCommandBuffer.h", 13, RenderProject, "Engine::Render::Vulkan");
            Add(items, "VulkanDescriptorSet", ItemKind.Class, @"Engine\Render\Vulkan", "VulkanDescriptorSet.h", 17, RenderProject, "Engine::Render::Vulkan");
            Add(items, "VulkanShaderModule", ItemKind.Class, @"Engine\Render\Vulkan", "VulkanShaderModule.h", 12, RenderProject, "Engine::Render::Vulkan");
            Add(items, "Renderer", ItemKind.Class, @"Engine\Render", "Renderer.h", 31, RenderProject, "Engine::Render");
            Add(items, "ArenaAllocator", ItemKind.Class, @"Engine\Core\Memory", "ArenaAllocator.h", 9, EngineProject, "Engine::Core");
            Add(items, "Logger", ItemKind.Class, @"Engine\Core\Logging", "Logger.h", 14, EngineProject, "Engine::Core");
            Add(items, "StringView", ItemKind.Class, @"Engine\Core\Text", "StringView.h", 7, EngineProject, "Engine::Core");
            Add(items, "DeviceMemory", ItemKind.Class, @"Engine\Render\Vulkan", "DeviceMemory.h", 15, RenderProject, "Engine::Render::Vulkan");
            Add(items, "DeviceContext", ItemKind.Class, @"Engine\Render\Vulkan", "DeviceContext.h", 22, RenderProject, "Engine::Render::Vulkan");
            Add(items, "IDeviceResource", ItemKind.Interface, @"Engine\Render\Vulkan", "IDeviceResource.h", 8, RenderProject, "Engine::Render::Vulkan");

            Add(items, "DeviceInfo", ItemKind.Struct, @"Engine\Render\Vulkan", "DeviceInfo.h", 36, RenderProject, "Engine::Render::Vulkan");
            Add(items, "DeviceState", ItemKind.Union, @"Engine\Render\Vulkan", "DeviceState.h", 19, RenderProject, "Engine::Render::Vulkan");
            Add(items, "SwapchainExtent", ItemKind.Struct, @"Engine\Render\Vulkan", "VulkanSwapchain.h", 52, RenderProject, "Engine::Render::Vulkan");
            Add(items, "SwapchainCreateInfo", ItemKind.Struct, @"Engine\Render\Vulkan", "VulkanSwapchain.h", 61, RenderProject, "Engine::Render::Vulkan");
            Add(items, "QueueFamilyIndices", ItemKind.Struct, @"Engine\Render\Vulkan", "VulkanPhysicalDevice.h", 44, RenderProject, "Engine::Render::Vulkan");
            Add(items, "FrameContext", ItemKind.Struct, @"Engine\Render", "FrameContext.h", 12, RenderProject, "Engine::Render");
            Add(items, "RenderPassInfo", ItemKind.Struct, @"Engine\Render", "RenderPassInfo.h", 18, RenderProject, "Engine::Render");
            Add(items, "MemoryBlock", ItemKind.Struct, @"Engine\Core\Memory", "MemoryBlock.h", 10, EngineProject, "Engine::Core");
            Add(items, "Vertex", ItemKind.Struct, @"Engine\Render\Mesh", "Vertex.h", 12, RenderProject, "Engine::Render");

            Add(items, "VulkanFormat", ItemKind.Enum, @"Engine\Render\Vulkan", "VulkanFormat.h", 7, RenderProject, "Engine::Render::Vulkan");
            Add(items, "VulkanResult", ItemKind.Enum, @"Engine\Render\Vulkan", "VulkanResult.h", 9, RenderProject, "Engine::Render::Vulkan");
            Add(items, "LogLevel", ItemKind.Enum, @"Engine\Core\Logging", "Logger.h", 22, EngineProject, "Engine::Core");
            Add(items, "DeviceType", ItemKind.Enum, @"Engine\Render\Vulkan", "DeviceInfo.h", 12, RenderProject, "Engine::Render::Vulkan");
            Add(items, "BGRA8_UNORM", ItemKind.EnumMember, @"Engine\Render\Vulkan", "VulkanFormat.h", 21, RenderProject, "VulkanFormat");
            Add(items, "DiscreteGpu", ItemKind.EnumMember, @"Engine\Render\Vulkan", "DeviceInfo.h", 15, RenderProject, "DeviceType");

            Add(items, "DeviceHandle", ItemKind.TypeAlias, @"Engine\Render\Vulkan", "VulkanTypes.h", 24, RenderProject, "Engine::Render::Vulkan");
            Add(items, "FrameIndex", ItemKind.TypeAlias, @"Engine\Render", "FrameContext.h", 8, RenderProject, "Engine::Render");

            // ----- callables -----------------------------------------------------------------
            Add(items, "CreateVulkanDevice", ItemKind.Method, @"Engine\Render\Vulkan", "VulkanDevice.cpp", 87, RenderProject, "VulkanDevice");
            Add(items, "DestroyVulkanDevice", ItemKind.Method, @"Engine\Render\Vulkan", "VulkanDevice.cpp", 118, RenderProject, "VulkanDevice");
            Add(items, "GetDeviceProperties", ItemKind.Method, @"Engine\Render\Vulkan", "VulkanDevice.cpp", 264, RenderProject, "VulkanDevice");
            Add(items, "WaitForDeviceIdle", ItemKind.Method, @"Engine\Render\Vulkan", "VulkanDevice.cpp", 341, RenderProject, "VulkanDevice");
            Add(items, "CreateDevice", ItemKind.Function, @"Engine\Render\Vulkan", "VulkanDeviceFactory.cpp", 46, RenderProject, "Engine::Render::Vulkan");
            Add(items, "CreateSwapchain", ItemKind.Method, @"Engine\Render\Vulkan", "VulkanSwapchain.cpp", 142, RenderProject, "VulkanSwapchain");
            Add(items, "DestroySwapchain", ItemKind.Method, @"Engine\Render\Vulkan", "VulkanSwapchain.cpp", 209, RenderProject, "VulkanSwapchain");
            Add(items, "CreateInstance", ItemKind.Method, @"Engine\Render\Vulkan", "VulkanInstance.cpp", 63, RenderProject, "VulkanInstance");
            Add(items, "PickPhysicalDevice", ItemKind.Method, @"Engine\Render\Vulkan", "VulkanInstance.cpp", 174, RenderProject, "VulkanInstance");
            Add(items, "CreateCommandBuffer", ItemKind.Method, @"Engine\Render\Vulkan", "VulkanCommandBuffer.cpp", 41, RenderProject, "VulkanCommandBuffer");
            Add(items, "SubmitFrame", ItemKind.Method, @"Engine\Render\Vulkan", "VulkanQueue.cpp", 58, RenderProject, "VulkanQueue");
            Add(items, "AllocateDeviceMemory", ItemKind.Method, @"Engine\Render\Vulkan", "VulkanAllocator.cpp", 96, RenderProject, "VulkanAllocator");
            Add(items, "FreeDeviceMemory", ItemKind.Method, @"Engine\Render\Vulkan", "VulkanAllocator.cpp", 137, RenderProject, "VulkanAllocator");
            Add(items, "BeginFrame", ItemKind.Method, @"Engine\Render", "Renderer.cpp", 210, RenderProject, "Renderer");
            Add(items, "EndFrame", ItemKind.Method, @"Engine\Render", "Renderer.cpp", 248, RenderProject, "Renderer");
            Add(items, "RenderScene", ItemKind.Method, @"Engine\Render", "Renderer.cpp", 301, RenderProject, "Renderer");
            Add(items, "LoadShader", ItemKind.Function, @"Engine\Render\Shader", "ShaderCompiler.cpp", 64, RenderProject, "Engine::Render");
            Add(items, "CompileShader", ItemKind.Function, @"Engine\Render\Shader", "ShaderCompiler.cpp", 118, RenderProject, "Engine::Render");
            Add(items, "vkCreateSwapchainKHR", ItemKind.Function, @"ThirdParty\volk", "volk.h", 8231, ThirdPartyProject, "volk");
            Add(items, "VulkanDevice", ItemKind.Constructor, @"Engine\Render\Vulkan", "VulkanDevice.cpp", 31, RenderProject, "VulkanDevice");
            Add(items, "~VulkanDevice", ItemKind.Destructor, @"Engine\Render\Vulkan", "VulkanDevice.cpp", 74, RenderProject, "VulkanDevice");

            // ----- variables / fields --------------------------------------------------------
            Add(items, "deviceCount", ItemKind.Field, @"Engine\Render\Vulkan", "VulkanDevice.h", 121, RenderProject, "VulkanDevice");
            Add(items, "physicalDevice", ItemKind.Field, @"Engine\Render\Vulkan", "VulkanDevice.h", 96, RenderProject, "VulkanDevice");
            Add(items, "gDeviceAllocator", ItemKind.GlobalVariable, @"Engine\Render\Vulkan", "VulkanAllocator.cpp", 18, RenderProject, "Engine::Render::Vulkan");
            Add(items, "frameIndex", ItemKind.Field, @"Engine\Render", "FrameContext.h", 29, RenderProject, "FrameContext");

            // ----- macros / concepts / templates ---------------------------------------------
            Add(items, "VK_CHECK", ItemKind.Macro, @"Engine\Core\Logging", "VulkanCheck.h", 12, EngineProject, "");
            Add(items, "VK_ASSERT", ItemKind.Macro, @"Engine\Core\Logging", "VulkanCheck.h", 26, EngineProject, "");
            Add(items, "VK_DEVICE_EXT", ItemKind.Macro, @"Engine\Render\Vulkan", "VulkanExtensions.h", 9, RenderProject, "");
            Add(items, "DEVICE_SAFE_RELEASE", ItemKind.Macro, @"Engine\Render\Vulkan", "VulkanDevice.h", 44, RenderProject, "");
            Add(items, "LOG_DEVICE", ItemKind.Macro, @"Engine\Core\Logging", "VulkanCheck.h", 41, EngineProject, "");
            Add(items, "DeviceConcept", ItemKind.Concept, @"Engine\Render\Vulkan", "DeviceConcept.h", 14, RenderProject, "Engine::Render::Vulkan");
            Add(items, "DeviceContainer", ItemKind.Template, @"Engine\Render\Vulkan", "DeviceContainer.h", 23, RenderProject, "Engine::Render::Vulkan");

            // ----- files ---------------------------------------------------------------------
            AddFile(items, @"Engine\Render\Vulkan", "VulkanDevice.h", RenderProject);
            AddFile(items, @"Engine\Render\Vulkan", "VulkanDevice.cpp", RenderProject);
            AddFile(items, @"Engine\Render\Vulkan", "VulkanInstance.h", RenderProject);
            AddFile(items, @"Engine\Render\Vulkan", "VulkanInstance.cpp", RenderProject);
            AddFile(items, @"Engine\Render\Vulkan", "VulkanSwapchain.h", RenderProject);
            AddFile(items, @"Engine\Render\Vulkan", "VulkanSwapchain.cpp", RenderProject);
            AddFile(items, @"Engine\Render\Vulkan", "VulkanPhysicalDevice.h", RenderProject);
            AddFile(items, @"Engine\Render\Vulkan", "VulkanPhysicalDevice.cpp", RenderProject);
            AddFile(items, @"Engine\Render", "Renderer.h", RenderProject);
            AddFile(items, @"Engine\Render", "Renderer.cpp", RenderProject);
            AddFile(items, @"Engine\Render\Shader", "ShaderCompiler.cpp", RenderProject);
            AddFile(items, @"Engine\Core\Memory", "ArenaAllocator.h", EngineProject);
            AddFile(items, @"Engine\Core\Logging", "Logger.cpp", EngineProject);
            AddFile(items, @"ThirdParty\vulkan", "vulkan.h", ThirdPartyProject);
            AddFile(items, @"ThirdParty\volk", "volk.h", ThirdPartyProject);

            return items;
        }

        private static void Add(
            List<NavigationItem> items,
            string name,
            ItemKind kind,
            string relativeDirectory,
            string fileName,
            int line,
            string project,
            string container)
        {
            string filePath = Path.Combine(BaseDirectory, relativeDirectory, fileName);
            var item = new NavigationItem(name, kind, filePath, line)
            {
                FullName = container.Length > 0 ? container + "::" + name : name,
                ContainerName = container,
                ProjectName = project,
                ProjectPath = Path.Combine(BaseDirectory, project, project + ".vcxproj"),
                Flags = ItemFlags.Public | ItemFlags.Exported,
                Language = "cpp",
            };
            items.Add(item);
        }

        private static void AddFile(List<NavigationItem> items, string relativeDirectory, string fileName, string project)
        {
            string filePath = Path.Combine(BaseDirectory, relativeDirectory, fileName);
            var item = new NavigationItem(fileName, ItemKind.File, filePath, 0)
            {
                FullName = filePath,
                FilePath = filePath,
                ProjectName = project,
                ProjectPath = Path.Combine(BaseDirectory, project, project + ".vcxproj"),
                Flags = ItemFlags.Public,
                Language = "cpp",
            };
            items.Add(item);
        }
    }
}
