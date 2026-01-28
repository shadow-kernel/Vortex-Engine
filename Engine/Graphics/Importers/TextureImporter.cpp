#include "TextureImporter.h"

#define STB_IMAGE_IMPLEMENTATION
#include "../../ThirdParty/stb_image.h"

#include <algorithm>

namespace vortex::graphics
{
	ImageData TextureImporter::import_from_file(const std::string& filepath, bool flip_vertically)
	{
	ImageData result;

	// Don't flip here - Assimp already handles UV flipping with aiProcess_FlipUVs
	// Setting flip_vertically=false by default to avoid double-flip issues
	stbi_set_flip_vertically_on_load(flip_vertically ? 1 : 0);

	int width, height, channels;
	// Request 4 channels (RGBA) to ensure consistent format for GPU upload
	unsigned char* data = stbi_load(filepath.c_str(), &width, &height, &channels, 4);

		if (!data)
		{
		return result; // Return empty on error
		}

		result.width = static_cast<u32>(width);
		result.height = static_cast<u32>(height);
		result.channels = 4; // Always 4 channels since we requested RGBA
		result.format = ImageFormat::RGBA8;

		// Copy data into vector - always RGBA now
		size_t data_size = width * height * 4;
		result.pixels.resize(data_size);
		memcpy(result.pixels.data(), data, data_size);

		// Free stb_image data
		stbi_image_free(data);

		return result;
	}

	bool TextureImporter::is_format_supported(const std::string& extension)
	{
		static const std::vector<std::string> supported = {
			".png", ".jpg", ".jpeg", ".tga", ".bmp", ".psd", ".hdr"
		};

		std::string ext_lower = extension;
		std::transform(ext_lower.begin(), ext_lower.end(), ext_lower.begin(), ::tolower);

		return std::find(supported.begin(), supported.end(), ext_lower) != supported.end();
	}

	ImageFormat TextureImporter::determine_format(u32 channels)
	{
		switch (channels)
		{
		case 1: return ImageFormat::R8;
		case 2: return ImageFormat::RG8;
		case 3: return ImageFormat::RGB8;
		case 4: return ImageFormat::RGBA8;
		default: return ImageFormat::Unknown;
		}
	}
}
